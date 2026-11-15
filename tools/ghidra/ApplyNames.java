// Applies the names the symbol database knows for a game executable before Ghidra analyses it.
// Argument: a tab-separated file written by GateKit (`gatekit decomp run`, the symbol database's Ghidra export)
// (rva, kind, name, decorated name or empty, source).
//
// A second argument says the program was analysed before:
//   update            only functions that have no name yet are named;
//   rename <touched>  every line is applied, over a name the address already has (the file holds only
//                     what was renamed since the last pass), and the functions whose pseudo-code shows
//                     the address - the function itself and those that refer to it - are appended to
//                     <touched>, one RVA in hex per line, for ExportDecomp.java to decompile again.
//
// A name that comes from a Toolkit export is applied in its decorated form, so that Ghidra's
// Microsoft demangler turns it into a full signature with parameter and return types.
//
// A name Ghidra refuses is written with the reason to <names file>.refused (rva, name, reason),
// so that `gatekit decomp refresh` can say which and leave them out of what it remembers.
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
import java.util.Map;
import java.util.TreeSet;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.CommentType;
import ghidra.program.model.symbol.SourceType;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.app.cmd.label.DemanglerCmd;

public class ApplyNames extends GhidraScript {

    private static String plain(String name) {
        StringBuilder b = new StringBuilder();
        for (char c : name.toCharArray()) {
            b.append(c == ' ' || c < 0x21 || c > 0x7e ? '_' : c);
        }
        String s = b.toString();
        return s.length() > 480 ? s.substring(0, 480) : s;
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            println("ApplyNames: no names file given");
            println("gatekit-script: ApplyNames failed no names file given");
            return;
        }
        // Analyzers that cost hours on a 100 MB image and add little to the pseudo-code.
        Map<String, String> options = Map.of(
            "Decompiler Parameter ID", "false",
            "Aggressive Instruction Finder", "false",
            "PDB Universal", "false",
            "PDB MSDIA", "false",
            "Windows x86 PE RTTI Analyzer", "false",
            "Embedded Media", "false",
            // Ran out of a 25 GB heap on bg3.exe (AddressSet.subtract in AddressTableAnalyzer).
            "Create Address Tables", "false",
            "Windows x86 PE Exception Handling", "true");
        for (Map.Entry<String, String> e : options.entrySet()) {
            try {
                setAnalysisOption(currentProgram, e.getKey(), e.getValue());
            } catch (Exception ignored) {
                // option does not exist in this Ghidra version
            }
        }
        // A second argument marks a program that was analysed before: only functions that have no
        // name yet are touched, and decorated names are demangled here, since no analysis follows.
        boolean rename = args.length > 2 && args[1].equals("rename");
        boolean update = args.length > 1 && !rename;
        boolean analysed = update || rename;
        TreeSet<Long> touched = new TreeSet<>();
        Address base = currentProgram.getImageBase();
        int applied = 0, failed = 0, kept = 0;
        List<String> refused = new ArrayList<>();
        try (BufferedReader r = new BufferedReader(new InputStreamReader(new FileInputStream(new File(args[0])), "UTF-8"))) {
            String line;
            while ((line = r.readLine()) != null) {
                String[] p = line.split("\t", -1);
                if (p.length < 5) {
                    continue;
                }
                try {
                    Address addr = base.add(Long.parseLong(p[0], 16));
                    boolean function = p[1].equals("function");
                    if (update) {
                        if (!function) {
                            continue;
                        }
                        Function known = getFunctionAt(addr);
                        if (known != null && known.getSymbol().getSource() != SourceType.DEFAULT) {
                            kept++;
                            continue;
                        }
                    }
                    if (function) {
                        disassemble(addr);
                        if (getFunctionAt(addr) == null) {
                            createFunction(addr, null);
                        }
                    }
                    // Decorated names only on functions: on data the demangler tries to build the
                    // whole type (a virtual table, a template instance) and that costs memory.
                    String label = p[3].isEmpty() || !function ? plain(p[2]) : p[3];
                    createLabel(addr, label, true, SourceType.IMPORTED);
                    if (analysed && function && !p[3].isEmpty()) {
                        new DemanglerCmd(addr, p[3]).applyTo(currentProgram);
                    }
                    currentProgram.getListing().setComment(addr, CommentType.PLATE, p[2] + "\n[" + p[4] + "]");
                    applied++;
                    if (rename) {
                        touch(touched, addr, base);
                    }
                } catch (Exception e) {
                    failed++;
                    refused.add(p[0] + "\t" + p[2] + "\t" + e.getClass().getSimpleName() + ": "
                                + String.valueOf(e.getMessage()).replace('\t', ' ').replace('\n', ' '));
                }
            }
        }
        try (BufferedWriter w = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(args[0] + ".refused", false), "UTF-8"))) {
            for (String line : refused) {
                w.write(line);
                w.write('\n');
            }
        }
        if (rename) {
            try (BufferedWriter w = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(args[2], true), "UTF-8"))) {
                for (long rva : touched) {
                    w.write(Long.toHexString(rva).toUpperCase());
                    w.write('\n');
                }
            }
        }
        println("ApplyNames: " + applied + " names applied, " + failed + " failed" + (update ? ", " + kept + " functions already named" : "")
                + (rename ? ", " + touched.size() + " functions show them" : ""));
        println("gatekit-script: ApplyNames done applied=" + applied + " failed=" + failed + " touched=" + touched.size());
    }

    // The functions whose pseudo-code shows a name: the function that starts at the address, every
    // function that refers to it, and the same for the thunks that only jump to it.
    private void touch(TreeSet<Long> touched, Address addr, Address base) {
        addReferrers(touched, addr, base);
        Function f = getFunctionAt(addr);
        if (f == null) {
            return;
        }
        touched.add(addr.subtract(base));
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
