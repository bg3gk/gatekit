// Applies the names the symbol database knows for a game executable before Ghidra analyses it.
// Argument: a tab-separated file written by GateKit (`gatekit decomp run`, the symbol database's Ghidra export)
// (rva, kind, name, decorated name or empty, source).
//
// A name that comes from a Toolkit export is applied in its decorated form, so that Ghidra's
// Microsoft demangler turns it into a full signature with parameter and return types.
//@category GateKit

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStreamReader;
import java.util.Map;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.CommentType;
import ghidra.program.model.symbol.SourceType;
import ghidra.program.model.listing.Function;
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
        boolean update = args.length > 1;
        Address base = currentProgram.getImageBase();
        int applied = 0, failed = 0, kept = 0;
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
                    if (update && function && !p[3].isEmpty()) {
                        new DemanglerCmd(addr, p[3]).applyTo(currentProgram);
                    }
                    currentProgram.getListing().setComment(addr, CommentType.PLATE, p[2] + "\n[" + p[4] + "]");
                    applied++;
                } catch (Exception e) {
                    failed++;
                }
            }
        }
        println("ApplyNames: " + applied + " names applied, " + failed + " failed" + (update ? ", " + kept + " functions already named" : ""));
    }
}
