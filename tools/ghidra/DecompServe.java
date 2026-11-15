// Keeps the decompiler open on a program and decompiles one function at a time on request, with
// the structures and parameter types a request brings applied first: `gatekit decomp serve`.
// Arguments: the channel folder, then the minutes without a request after which it ends.
//
// The channel: a request is a file <id>.request (tab-separated lines, written whole by moving it
// into place), answered by <id>.answer, written the same way. Lines of a request:
//   type    structure, size, offset in hex, field size, kind, field name, type (as ApplyTypes.java)
//   param   rva in hex, parameter index, structure: the parameter becomes a pointer to it
//   function rva in hex: the function to decompile (one a request)
// The answer's first line is `ok <apply ms> <decompile ms>` or `error <message>`, tab-separated;
// the pseudo-code follows. `ready` holds the milliseconds the opening took; `stop` ends it.
// The program is opened read-only by the command: nothing applied here is saved.
//@category GateKit

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.ArrayDataType;
import ghidra.program.model.data.BooleanDataType;
import ghidra.program.model.data.ByteDataType;
import ghidra.program.model.data.CategoryPath;
import ghidra.program.model.data.DataType;
import ghidra.program.model.data.DataTypeConflictHandler;
import ghidra.program.model.data.DataTypeManager;
import ghidra.program.model.data.DoubleDataType;
import ghidra.program.model.data.FloatDataType;
import ghidra.program.model.data.IntegerDataType;
import ghidra.program.model.data.LongLongDataType;
import ghidra.program.model.data.PointerDataType;
import ghidra.program.model.data.ShortDataType;
import ghidra.program.model.data.SignedByteDataType;
import ghidra.program.model.data.Structure;
import ghidra.program.model.data.StructureDataType;
import ghidra.program.model.data.Undefined;
import ghidra.program.model.data.UnsignedIntegerDataType;
import ghidra.program.model.data.UnsignedLongLongDataType;
import ghidra.program.model.data.UnsignedShortDataType;
import ghidra.program.model.listing.Function;
import ghidra.program.model.pcode.HighFunction;
import ghidra.program.model.pcode.HighFunctionDBUtil;
import ghidra.program.model.pcode.HighFunctionDBUtil.ReturnCommitOption;
import ghidra.program.model.symbol.SourceType;

public class DecompServe extends GhidraScript {

    private static final class Field {
        int offset, size;
        String kind, name, type;
    }

    private static final class Type {
        String name;
        int size;
        List<Field> fields = new ArrayList<>();
    }

    private static DataType bytes(int size) {
        if (size == 1 || size == 2 || size == 4 || size == 8) {
            return Undefined.getUndefinedDataType(size);
        }
        return new ArrayDataType(Undefined.getUndefinedDataType(1), size, 1);
    }

    // the same data types ApplyTypes.java makes of a field
    private static DataType dataType(Field f) {
        switch (f.kind) {
            case "bool": return f.size == 1 ? BooleanDataType.dataType : bytes(f.size);
            case "ptr": return f.size == 8 ? PointerDataType.dataType : bytes(f.size);
            case "int":
                switch (f.size) {
                    case 1: return SignedByteDataType.dataType;
                    case 2: return ShortDataType.dataType;
                    case 4: return IntegerDataType.dataType;
                    case 8: return LongLongDataType.dataType;
                    default: return bytes(f.size);
                }
            case "uint":
                switch (f.size) {
                    case 1: return ByteDataType.dataType;
                    case 2: return UnsignedShortDataType.dataType;
                    case 4: return UnsignedIntegerDataType.dataType;
                    case 8: return UnsignedLongLongDataType.dataType;
                    default: return bytes(f.size);
                }
            case "float":
                if (f.size == 4) return FloatDataType.dataType;
                if (f.size == 8 && f.type.equals("double")) return DoubleDataType.dataType;
                return f.size % 4 == 0 ? new ArrayDataType(FloatDataType.dataType, f.size / 4, 4) : bytes(f.size);
            default: return bytes(f.size);
        }
    }

    private static String[] split(String name) {
        int cut = name.indexOf('<') >= 0 ? -1 : name.lastIndexOf("::");
        if (cut < 0) return new String[] { "", name };
        return new String[] { "/" + name.substring(0, cut).replace("::", "/"), name.substring(cut + 2) };
    }

    private Structure structure(DataTypeManager dtm, String name, Type t) {
        String[] parts = split(name);
        List<DataType> found = new ArrayList<>();
        dtm.findDataTypes(parts[1], found);
        Structure s = null;
        for (DataType d : found) {
            if (d instanceof Structure && d.getCategoryPath().getPath().endsWith(parts[0])) {
                s = (Structure) d;
                break;
            }
        }
        if (s == null && t != null) {
            StructureDataType fresh = new StructureDataType(new CategoryPath(parts[0].isEmpty() ? "/" : parts[0]), parts[1], 0, dtm);
            s = (Structure) dtm.addDataType(fresh, DataTypeConflictHandler.REPLACE_HANDLER);
        }
        if (s != null && t != null) {
            s.deleteAll();
            if (t.size > 0) s.growStructure(t.size);
            for (Field f : t.fields) {
                if (f.size <= 0 || f.offset + f.size > s.getLength()) continue;
                s.replaceAtOffset(f.offset, dataType(f), f.size, f.name, f.type.isEmpty() ? null : f.type);
            }
        }
        return s;
    }

    private static void write(File channel, String name, String text) throws Exception {
        File temporary = new File(channel, name + ".writing");
        try (BufferedWriter w = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(temporary), "UTF-8"))) {
            w.write(text);
        }
        Files.move(temporary.toPath(), new File(channel, name).toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
    }

    private String answer(DecompInterface decompiler, List<String[]> lines) throws Exception {
        long started = System.nanoTime();
        Address base = currentProgram.getImageBase();
        DataTypeManager dtm = currentProgram.getDataTypeManager();
        Map<String, Type> types = new LinkedHashMap<>();
        Function target = null;
        for (String[] p : lines) {
            if (p[0].equals("type") && p.length >= 8) {
                Type t = types.get(p[1]);
                if (t == null) {
                    t = new Type();
                    t.name = p[1];
                    t.size = Integer.parseInt(p[2]);
                    types.put(p[1], t);
                }
                Field f = new Field();
                f.offset = Integer.parseInt(p[3], 16);
                f.size = Integer.parseInt(p[4]);
                f.kind = p[5];
                f.name = p[6];
                f.type = p[7];
                if (!f.name.equals("_")) t.fields.add(f);
            } else if (p[0].equals("function") && p.length >= 2) {
                target = getFunctionAt(base.add(Long.parseLong(p[1], 16)));
                if (target == null) return "error\tno function starts at +0x" + p[1] + "\n";
            }
        }
        boolean changed = false;
        for (Type t : types.values()) {
            structure(dtm, t.name, t);
            changed = true;
        }
        for (String[] p : lines) {
            if (!p[0].equals("param") || p.length < 4) continue;
            Function f = getFunctionAt(base.add(Long.parseLong(p[1], 16)));
            Structure s = structure(dtm, p[3], null);
            if (f == null || s == null) continue;
            int index = Integer.parseInt(p[2]);
            if (f.getParameterCount() <= index) {
                DecompileResults res = decompiler.decompileFunction(f, 60, monitor);
                HighFunction high = res == null ? null : res.getHighFunction();
                if (high != null) HighFunctionDBUtil.commitParamsToDatabase(high, true, ReturnCommitOption.NO_COMMIT, SourceType.ANALYSIS);
            }
            if (f.getParameterCount() > index && f.getParameter(index).getLength() == 8) {
                f.getParameter(index).setDataType(new PointerDataType(s, dtm), SourceType.USER_DEFINED);
                changed = true;
            }
        }
        if (changed) decompiler.flushCache();
        long applied = System.nanoTime();
        if (target == null) return "error\tthe request names no function\n";
        DecompileResults results = decompiler.decompileFunction(target, 90, monitor);
        long decompiled = System.nanoTime();
        if (results == null || !results.decompileCompleted() || results.getDecompiledFunction() == null) {
            return "error\t" + (results == null ? "no result" : String.valueOf(results.getErrorMessage()).replace('\n', ' ')) + "\n";
        }
        return "ok\t" + (applied - started) / 1000000 + "\t" + (decompiled - applied) / 1000000 + "\n" + results.getDecompiledFunction().getC();
    }

    @Override
    public void run() throws Exception {
        long opening = System.nanoTime();
        String[] args = getScriptArgs();
        if (args.length < 1) {
            println("gatekit-script: DecompServe failed needs the channel folder");
            return;
        }
        File channel = new File(args[0]);
        long idle = (args.length > 1 ? Long.parseLong(args[1]) : 30) * 60000L;
        channel.mkdirs();
        DecompInterface decompiler = new DecompInterface();
        try {
            DecompileOptions options = new DecompileOptions();
            options.grabFromProgram(currentProgram);
            decompiler.setOptions(options);
            decompiler.toggleCCode(true);
            decompiler.toggleSyntaxTree(true);
            decompiler.setSimplificationStyle("decompile");
            decompiler.openProgram(currentProgram);
            write(channel, "ready", Long.toString((System.nanoTime() - opening) / 1000000) + "\n");
            println("DecompServe: ready on " + channel);
            long last = System.currentTimeMillis();
            while (!monitor.isCancelled() && !new File(channel, "stop").exists() && System.currentTimeMillis() - last < idle) {
                File[] requests = channel.listFiles((dir, name) -> name.endsWith(".request"));
                if (requests == null || requests.length == 0) {
                    Thread.sleep(50);
                    continue;
                }
                for (File request : requests) {
                    String id = request.getName().substring(0, request.getName().length() - ".request".length());
                    List<String[]> lines = new ArrayList<>();
                    try (BufferedReader r = new BufferedReader(new InputStreamReader(new FileInputStream(request), "UTF-8"))) {
                        String line;
                        while ((line = r.readLine()) != null) {
                            if (!line.isEmpty()) lines.add(line.split("\t", -1));
                        }
                    }
                    request.delete();
                    String text;
                    try {
                        text = answer(decompiler, lines);
                    } catch (Exception e) {
                        text = "error\t" + e.getClass().getSimpleName() + ": " + String.valueOf(e.getMessage()).replace('\n', ' ') + "\n";
                    }
                    write(channel, id + ".answer", text);
                    last = System.currentTimeMillis();
                }
            }
        } finally {
            decompiler.dispose();
            new File(channel, "ready").delete();
            new File(channel, "stop").delete();
        }
        println("gatekit-script: DecompServe done");
    }
}
