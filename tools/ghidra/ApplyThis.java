// Tells the decompiler what the first parameter of a function points at: for the functions that
// take an ECS system first (its update entry, its update, its constructor) a pointer to the
// system's structure, so that `param_1 + 0x38` reads `this->field_0x38`; for the registry's
// applied functions, a pointer to the applied structure their first parameter is tied to.
// Arguments: a tab-separated file written by `gatekit decomp refresh`
// (rva, role, system, structure, basis), then the file to append the touched functions to: one
// RVA in hex per line, for ExportDecomp.java.
// What could not be applied is written to <file>.refused, one line per function: rva, system, reason.
//
// Run after ApplyTypes.java, which creates the structures: one is found the way that script finds
// it (by its name, in a category that ends with its namespace). The function is decompiled once
// and the parameters the decompiler sees are committed, so that the first one exists to be typed.
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
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Parameter;
import ghidra.program.model.pcode.HighFunction;
import ghidra.program.model.pcode.HighFunctionDBUtil;
import ghidra.program.model.pcode.HighFunctionDBUtil.ReturnCommitOption;
import ghidra.program.model.symbol.SourceType;

public class ApplyThis extends GhidraScript {

    // `ecl::activation::CullSystem` -> {"/ecl/activation", "CullSystem"}, as ApplyTypes.java splits it.
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

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("ApplyThis: needs the functions file and the file of touched functions");
            println("gatekit-script: ApplyThis failed needs the functions file and the file of touched functions");
            return;
        }
        Address base = currentProgram.getImageBase();
        DataTypeManager dtm = currentProgram.getDataTypeManager();
        TreeSet<Long> touched = new TreeSet<>();
        List<String> refused = new ArrayList<>();
        int typed = 0;
        DecompInterface decompiler = new DecompInterface();
        try (BufferedReader r = new BufferedReader(new InputStreamReader(new FileInputStream(new File(args[0])), "UTF-8"))) {
            decompiler.setOptions(new DecompileOptions());
            decompiler.toggleCCode(false);
            decompiler.openProgram(currentProgram);
            String line;
            while ((line = r.readLine()) != null) {
                if (monitor.isCancelled()) {
                    break;
                }
                String[] p = line.split("\t", -1);
                if (p.length < 5) {
                    continue;
                }
                try {
                    Function f = getFunctionAt(base.add(Long.parseLong(p[0], 16)));
                    if (f == null) {
                        refused.add(p[0] + "\t" + p[2] + "\tno function starts at the address");
                        continue;
                    }
                    Structure s = structure(dtm, p[3]);
                    if (s == null) {
                        refused.add(p[0] + "\t" + p[2] + "\tthe program has no structure of that name");
                        continue;
                    }
                    if (f.getParameterCount() == 0) {
                        DecompileResults res = decompiler.decompileFunction(f, 60, monitor);
                        HighFunction high = res == null ? null : res.getHighFunction();
                        if (high != null) {
                            HighFunctionDBUtil.commitParamsToDatabase(high, true, ReturnCommitOption.NO_COMMIT, SourceType.ANALYSIS);
                        }
                    }
                    if (f.getParameterCount() == 0) {
                        refused.add(p[0] + "\t" + p[2] + "\tthe decompiler sees no parameter");
                        continue;
                    }
                    Parameter first = f.getParameter(0);
                    if (first.getLength() != 8) {
                        refused.add(p[0] + "\t" + p[2] + "\tthe first parameter is not a pointer's size");
                        continue;
                    }
                    first.setDataType(new PointerDataType(s, dtm), SourceType.USER_DEFINED);
                    typed++;
                    touched.add(f.getEntryPoint().subtract(base));
                } catch (Exception e) {
                    refused.add(p[0] + "\t" + p[2] + "\t" + e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage()).replace('\t', ' ').replace('\n', ' '));
                }
            }
        } finally {
            decompiler.dispose();
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
        println("ApplyThis: " + typed + " functions take a system as their first parameter, " + refused.size() + " refused");
        println("gatekit-script: ApplyThis done typed=" + typed + " refused=" + refused.size() + " touched=" + touched.size());
    }
}
