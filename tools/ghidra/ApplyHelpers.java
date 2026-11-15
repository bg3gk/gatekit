// Names the small helpers through which the game reaches a component (get, has, add, remove,
// registration of one type), and gives those that hand the component back a pointer to its
// structure as their return type, so that a caller reads `pComponent->Field`.
// Arguments: a tab-separated file written by `gatekit decomp refresh`
// (rva, family, type, name, structure of the return type or empty, basis), then the file to append
// the touched functions to: one RVA in hex per line, for ExportDecomp.java.
// What could not be applied is written to <helpers file>.refused, one line per helper:
// rva, name, reason.
//
// Run after ApplyTypes.java, which creates the structures: one is found the way that script finds
// it (by its name, in a category that ends with its namespace). Before the return type is set the
// helper is decompiled once and the parameters the decompiler sees are committed, so that setting a
// return type, which fixes the prototype, does not take its parameters away.
// A function the symbol database names is never in the file; one that has a name of its own here
// (imported or given by a person) is left as it is and listed as refused. A line whose family is
// `-` takes a helper's name back: the function gets its default name, an undefined return type
// and a prototype the decompiler infers again (only when the name is one this script gives).
//@category GateKit

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.util.ArrayList;
import java.util.List;
import java.util.TreeSet;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.DataType;
import ghidra.program.model.data.DataTypeManager;
import ghidra.program.model.data.PointerDataType;
import ghidra.program.model.data.Structure;
import ghidra.program.model.listing.CommentType;
import ghidra.program.model.listing.Function;
import ghidra.program.model.pcode.HighFunction;
import ghidra.program.model.pcode.HighFunctionDBUtil;
import ghidra.program.model.pcode.HighFunctionDBUtil.ReturnCommitOption;
import ghidra.app.util.NamespaceUtils;
import ghidra.program.model.symbol.Namespace;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.SourceType;
import ghidra.program.model.symbol.SymbolUtilities;
import ghidra.util.exception.DuplicateNameException;

public class ApplyHelpers extends GhidraScript {

    private static String plain(String name) {
        StringBuilder b = new StringBuilder();
        for (char c : name.toCharArray()) {
            b.append(c == ' ' || c < 0x21 || c > 0x7e ? '_' : c);
        }
        String s = b.toString();
        return s.length() > 480 ? s.substring(0, 480) : s;
    }

    // `ecl::activation::CulledComponent` -> {"/ecl/activation", "CulledComponent"}, as ApplyTypes.java splits it.
    private static String[] split(String name) {
        int cut = name.indexOf('<') >= 0 ? -1 : name.lastIndexOf("::");
        if (cut < 0) {
            return new String[] { "", name };
        }
        return new String[] { "/" + name.substring(0, cut).replace("::", "/"), name.substring(cut + 2) };
    }

    private Structure structure(DataTypeManager dtm, String name) {
        String[] parts = split(name);
        List<DataType> found = new ArrayList<>();
        dtm.findDataTypes(parts[1], found);
        for (DataType d : found) {
            if (d instanceof Structure && d.getCategoryPath().getPath().endsWith(parts[0])) {
                return (Structure) d;
            }
        }
        return null;
    }

    // `ecs::GetComponent<eoc::X>` -> {"ecs", "GetComponent<eoc::X>"}: the cut is the last :: outside the template arguments.
    private static String[] cut(String name) {
        int depth = 0, at = -1;
        for (int i = 0; i + 1 < name.length(); i++) {
            char c = name.charAt(i);
            if (c == '<') {
                depth++;
            } else if (c == '>') {
                depth--;
            } else if (depth == 0 && c == ':' && name.charAt(i + 1) == ':') {
                at = i;
            }
        }
        return at < 0 ? new String[] { "", name } : new String[] { name.substring(0, at), name.substring(at + 2) };
    }

    // Names the function in its namespace (made when missing), as the demangler names a C++ function.
    private void rename(Function f, String full, String suffix) throws Exception {
        String[] parts = cut(full);
        Namespace ns = parts[0].isEmpty() ? currentProgram.getGlobalNamespace()
            : NamespaceUtils.createNamespaceHierarchy(parts[0], null, currentProgram, SourceType.IMPORTED);
        f.getSymbol().setNameAndNamespace(parts[1] + suffix, ns, SourceType.IMPORTED);
    }

    private static boolean ours(String name) {
        return name.startsWith("FUN_") || name.startsWith("thunk_FUN_") || name.startsWith("ecs::GetComponent<")
            || name.startsWith("ecs::HasComponent<") || name.startsWith("ecs::AddComponent<") || name.startsWith("ecs::RemoveComponent<")
            || name.startsWith("ecs::RegisterComponent<") || name.startsWith("ecs::ComponentHelper<");
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("ApplyHelpers: needs the helpers file and the file of touched functions");
            println("gatekit-script: ApplyHelpers failed needs the helpers file and the file of touched functions");
            return;
        }
        Address base = currentProgram.getImageBase();
        DataTypeManager dtm = currentProgram.getDataTypeManager();
        TreeSet<Long> touched = new TreeSet<>();
        List<String> refused = new ArrayList<>();
        int named = 0, typed = 0, suffixed = 0, noStructure = 0;
        DecompInterface decompiler = null;
        try (BufferedReader r = new BufferedReader(new InputStreamReader(new FileInputStream(new File(args[0])), "UTF-8"))) {
            String line;
            while ((line = r.readLine()) != null) {
                if (monitor.isCancelled()) {
                    break;
                }
                String[] p = line.split("\t", -1);
                if (p.length < 6) {
                    continue;
                }
                String name = plain(p[3]);
                try {
                    Address addr = base.add(Long.parseLong(p[0], 16));
                    Function f = getFunctionAt(addr);
                    if (f == null) {
                        disassemble(addr);
                        f = createFunction(addr, null);
                    }
                    if (f == null) {
                        refused.add(p[0] + "\t" + name + "\tno function starts at the address");
                        continue;
                    }
                    String had = f.getName(true);
                    if (p[1].equals("-")) {
                        if (ours(had) && f.getSymbol().getSource() != SourceType.DEFAULT) {
                            takeBack(f);
                            touch(touched, f, base);
                            named++;
                        }
                        continue;
                    }
                    if (!ours(had) && f.getSymbol().getSource() != SourceType.DEFAULT) {
                        refused.add(p[0] + "\t" + name + "\tthe function is already named " + had);
                        continue;
                    }
                    String[] parts = cut(name);
                    boolean same = f.getName().equals(parts[1]) && (parts[0].isEmpty() ? f.getParentNamespace().isGlobal()
                        : !f.getParentNamespace().isGlobal() && f.getParentNamespace().getName(true).equals(parts[0]));
                    if (!same) {
                        try {
                            rename(f, name, "");
                        } catch (DuplicateNameException e) {
                            boolean done = false;
                            for (int n = 2; n < 100 && !done; n++) {
                                try {
                                    rename(f, name, "_" + n);
                                    done = true;
                                    suffixed++;
                                } catch (DuplicateNameException again) {
                                    // the next number
                                }
                            }
                            if (!done) {
                                refused.add(p[0] + "\t" + name + "\tthe name is taken (and with _2 .. _99)");
                                continue;
                            }
                        }
                    }
                    currentProgram.getListing().setComment(addr, CommentType.PLATE, p[3] + "\n[GateKit helper: " + p[1] + "; " + p[5] + "]");
                    named++;
                    if (!p[4].isEmpty()) {
                        Structure s = structure(dtm, p[4]);
                        if (s == null) {
                            noStructure++;
                        } else {
                            if (decompiler == null) {
                                decompiler = new DecompInterface();
                                decompiler.setOptions(new DecompileOptions());
                                decompiler.toggleCCode(false);
                                decompiler.openProgram(currentProgram);
                            }
                            DecompileResults res = decompiler.decompileFunction(f, 60, monitor);
                            HighFunction high = res == null ? null : res.getHighFunction();
                            if (high == null) {
                                refused.add(p[0] + "\t" + name + "\tnot decompiled, so its parameters are not known: no return type");
                            } else {
                                HighFunctionDBUtil.commitParamsToDatabase(high, true, ReturnCommitOption.NO_COMMIT, SourceType.ANALYSIS);
                                f.setReturnType(new PointerDataType(s, dtm), SourceType.IMPORTED);
                                typed++;
                            }
                        }
                    }
                    touch(touched, f, base);
                } catch (Exception e) {
                    refused.add(p[0] + "\t" + name + "\t" + e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage()).replace('\t', ' ').replace('\n', ' '));
                }
            }
        } finally {
            if (decompiler != null) {
                decompiler.dispose();
            }
        }
        try (BufferedWriter w = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(args[1], true), "UTF-8"))) {
            for (long rva : touched) {
                w.write(Long.toHexString(rva).toUpperCase());
                w.write('\n');
            }
        }
        try (BufferedWriter w = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(args[0] + ".refused", false), "UTF-8"))) {
            for (String line : refused) {
                w.write(line);
                w.write('\n');
            }
        }
        println("ApplyHelpers: " + named + " helpers named (" + suffixed + " with a number), " + typed + " given a return type, "
                + noStructure + " without a structure, " + refused.size() + " refused, " + touched.size() + " functions show them");
        println("gatekit-script: ApplyHelpers done named=" + named + " typed=" + typed + " refused=" + refused.size() + " touched=" + touched.size());
    }

    // The default name again, in the global namespace; no return type; the prototype unlocked.
    private void takeBack(Function f) throws Exception {
        try {
            f.getSymbol().setNameAndNamespace("", currentProgram.getGlobalNamespace(), SourceType.DEFAULT);
        } catch (Exception e) {
            f.getSymbol().setNameAndNamespace(SymbolUtilities.getDefaultFunctionName(f.getEntryPoint()), currentProgram.getGlobalNamespace(), SourceType.ANALYSIS);
        }
        f.setReturnType(DataType.DEFAULT, SourceType.DEFAULT);
        f.setSignatureSource(SourceType.DEFAULT);
        currentProgram.getListing().setComment(f.getEntryPoint(), CommentType.PLATE, null);
    }

    // The helper, every function that refers to it, and the same for the thunks that only jump to it.
    private void touch(TreeSet<Long> touched, Function f, Address base) {
        touched.add(f.getEntryPoint().subtract(base));
        addReferrers(touched, f.getEntryPoint(), base);
        Address[] thunks = f.getFunctionThunkAddresses(true);
        if (thunks != null) {
            for (Address t : thunks) {
                touched.add(t.subtract(base));
                addReferrers(touched, t, base);
            }
        }
    }

    private void addReferrers(TreeSet<Long> touched, Address addr, Address base) {
        ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(addr);
        while (refs.hasNext()) {
            Reference r = refs.next();
            Function from = getFunctionContaining(r.getFromAddress());
            if (from != null) {
                touched.add(from.getEntryPoint().subtract(base));
            }
        }
    }
}
