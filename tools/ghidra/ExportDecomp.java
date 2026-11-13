// Decompiles every function of the program in parallel and writes one JSON object per line:
// {"rva": "1A2B3C", "name": ..., "sig": ..., "size": n, "callees": ["rva", ...], "code": "...", "error": ...}
// Arguments: output file [first rva (hex) [last rva (hex)]] - the range lets a run be split or resumed.
//@category GateKit

import java.io.BufferedWriter;
import java.io.FileOutputStream;
import java.io.OutputStreamWriter;
import java.util.concurrent.atomic.AtomicInteger;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.decompiler.parallel.DecompileConfigurer;
import ghidra.app.decompiler.parallel.DecompilerCallback;
import ghidra.app.decompiler.parallel.ParallelDecompiler;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.util.task.TaskMonitor;

public class ExportDecomp extends GhidraScript {

    private static String quote(String s) {
        if (s == null) {
            return "null";
        }
        StringBuilder b = new StringBuilder(s.length() + 16);
        b.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': break;
                case '\t': b.append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7e) {
                        b.append(String.format("\\u%04x", (int) c));
                    } else {
                        b.append(c);
                    }
            }
        }
        b.append('"');
        return b.toString();
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            println("ExportDecomp: no output file given");
            return;
        }
        final Address base = currentProgram.getImageBase();
        Address first = args.length > 1 ? base.add(Long.parseLong(args[1], 16)) : currentProgram.getMinAddress();
        Address last = args.length > 2 ? base.add(Long.parseLong(args[2], 16)) : currentProgram.getMaxAddress();
        AddressSet range = new AddressSet(first, last);

        DecompileConfigurer configurer = new DecompileConfigurer() {
            @Override
            public void configure(DecompInterface decompiler) {
                DecompileOptions options = new DecompileOptions();
                options.grabFromProgram(currentProgram);
                decompiler.setOptions(options);
                decompiler.toggleCCode(true);
                decompiler.toggleSyntaxTree(false);
                decompiler.setSimplificationStyle("decompile");
            }
        };
        final AtomicInteger done = new AtomicInteger();
        final AtomicInteger failed = new AtomicInteger();
        final TaskMonitor mon = monitor;
        DecompilerCallback<String> callback = new DecompilerCallback<String>(currentProgram, configurer) {
            @Override
            public String process(DecompileResults results, TaskMonitor m) throws Exception {
                Function f = results.getFunction();
                StringBuilder b = new StringBuilder(4096);
                b.append("{\"rva\":\"").append(Long.toHexString(f.getEntryPoint().subtract(base)).toUpperCase()).append('"');
                b.append(",\"name\":").append(quote(f.getName(true)));
                b.append(",\"sig\":").append(quote(f.getPrototypeString(false, false)));
                b.append(",\"size\":").append(f.getBody().getNumAddresses());
                b.append(",\"thunk\":").append(f.isThunk());
                b.append(",\"callees\":[");
                boolean firstCallee = true;
                for (Function c : f.getCalledFunctions(m)) {
                    if (c.isExternal()) {
                        continue;
                    }
                    b.append(firstCallee ? "\"" : ",\"").append(Long.toHexString(c.getEntryPoint().subtract(base)).toUpperCase()).append('"');
                    firstCallee = false;
                }
                b.append(']');
                if (results.decompileCompleted() && results.getDecompiledFunction() != null) {
                    b.append(",\"code\":").append(quote(results.getDecompiledFunction().getC()));
                } else {
                    failed.incrementAndGet();
                    b.append(",\"error\":").append(quote(results.getErrorMessage()));
                }
                b.append('}');
                return b.toString();
            }
        };
        callback.setTimeout(90);
        try (BufferedWriter out = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(args[0]), "UTF-8"), 1 << 20)) {
            java.util.Iterator<Function> functions = currentProgram.getListing().getFunctions(range, true);
            ParallelDecompiler.decompileFunctions(callback, currentProgram, functions, (String line) -> {
                if (line == null) {
                    return;
                }
                synchronized (out) {
                    try {
                        out.write(line);
                        out.write('\n');
                    } catch (Exception e) {
                        // disk full or closed: nothing sensible to do per line
                    }
                }
                int n = done.incrementAndGet();
                if (n % 5000 == 0) {
                    println("ExportDecomp: " + n + " functions");
                }
            }, mon);
        } finally {
            callback.dispose();
        }
        println("ExportDecomp: " + done.get() + " functions written, " + failed.get() + " not decompiled");
    }
}
