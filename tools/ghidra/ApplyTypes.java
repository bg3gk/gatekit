// Gives the structures of the program the field names GateKit's record holds, so that pseudo-code
// reads `this->Timer` where it read `*(float *)(this + 0)`.
// Arguments: a tab-separated file written by `gatekit decomp refresh`
// (structure, size, offset in hex, field size, kind, field name, the record's type), then the file
// to append the touched functions to: one RVA in hex per line, for ExportDecomp.java.
//
// A structure is found by its name in a category that ends with its namespace (the demangler puts
// `ecl::Foo` into /Demangler/ecl), every one that matches is filled, and one that does not exist
// yet is created as /ecl/Foo, where Ghidra looks for the structure of a class. The functions that
// are touched are those with a parameter or a return value of such a type: a pointer the
// decompiler was never told the type of stays `param_1 + 0x10`, whatever the record knows.
//@category GateKit

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.Array;
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
import ghidra.program.model.data.Pointer;
import ghidra.program.model.data.PointerDataType;
import ghidra.program.model.data.ShortDataType;
import ghidra.program.model.data.SignedByteDataType;
import ghidra.program.model.data.Structure;
import ghidra.program.model.data.StructureDataType;
import ghidra.program.model.data.TypeDef;
import ghidra.program.model.data.Undefined;
import ghidra.program.model.data.UnsignedIntegerDataType;
import ghidra.program.model.data.UnsignedLongLongDataType;
import ghidra.program.model.data.UnsignedShortDataType;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Parameter;

public class ApplyTypes extends GhidraScript {

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

    private static DataType dataType(Field f) {
        switch (f.kind) {
            case "bool":
                return f.size == 1 ? BooleanDataType.dataType : bytes(f.size);
            case "ptr":
                return f.size == 8 ? PointerDataType.dataType : bytes(f.size);
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
                if (f.size == 4) {
                    return FloatDataType.dataType;
                }
                if (f.size == 8 && f.type.equals("double")) {
                    return DoubleDataType.dataType;
                }
                return f.size % 4 == 0 ? new ArrayDataType(FloatDataType.dataType, f.size / 4, 4) : bytes(f.size);
            default:
                return bytes(f.size);
        }
    }

    // `ecl::activation::CulledComponent` -> {"/ecl/activation", "CulledComponent"}; a name with
    // template arguments is not split.
    private static String[] split(String name) {
        int cut = name.indexOf('<') >= 0 ? -1 : name.lastIndexOf("::");
        if (cut < 0) {
            return new String[] { "", name };
        }
        return new String[] { "/" + name.substring(0, cut).replace("::", "/"), name.substring(cut + 2) };
    }

    private static void fill(Structure s, Type t) {
        s.deleteAll();
        if (t.size > 0) {
            s.growStructure(t.size);
        }
        for (Field f : t.fields) {
            if (f.size <= 0 || f.offset + f.size > s.getLength()) {
                continue;
            }
            s.replaceAtOffset(f.offset, dataType(f), f.size, f.name, f.type.isEmpty() ? null : f.type);
        }
    }

    private static DataType base(DataType d) {
        for (int depth = 0; d != null && depth < 8; depth++) {
            if (d instanceof Pointer) {
                d = ((Pointer) d).getDataType();
            } else if (d instanceof TypeDef) {
                d = ((TypeDef) d).getBaseDataType();
            } else if (d instanceof Array) {
                d = ((Array) d).getDataType();
            } else {
                break;
            }
        }
        return d;
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("ApplyTypes: needs the types file and the file of touched functions");
            println("gatekit-script: ApplyTypes failed needs the types file and the file of touched functions");
            return;
        }
        Map<String, Type> types = new LinkedHashMap<>();
        try (BufferedReader r = new BufferedReader(new InputStreamReader(new FileInputStream(new File(args[0])), "UTF-8"))) {
            String line;
            while ((line = r.readLine()) != null) {
                String[] p = line.split("\t", -1);
                if (p.length < 7) {
                    continue;
                }
                try {
                    Type t = types.get(p[0]);
                    if (t == null) {
                        t = new Type();
                        t.name = p[0];
                        t.size = Integer.parseInt(p[1]);
                        types.put(p[0], t);
                    }
                    Field f = new Field();
                    f.offset = Integer.parseInt(p[2], 16);
                    f.size = Integer.parseInt(p[3]);
                    f.kind = p[4];
                    f.name = p[5];
                    f.type = p[6];
                    t.fields.add(f);
                } catch (NumberFormatException e) {
                    // a line this script does not understand: skipped
                }
            }
        }

        DataTypeManager dtm = currentProgram.getDataTypeManager();
        Set<String> changed = new HashSet<>();       // path names of the structures that were filled
        int filled = 0, created = 0, failed = 0;
        for (Type t : types.values()) {
            if (monitor.isCancelled()) {
                break;
            }
            try {
                String[] parts = split(t.name);
                List<DataType> found = new ArrayList<>();
                dtm.findDataTypes(parts[1], found);
                boolean any = false;
                for (DataType d : found) {
                    if (!(d instanceof Structure) || !d.getCategoryPath().getPath().endsWith(parts[0])) {
                        continue;
                    }
                    fill((Structure) d, t);
                    changed.add(d.getPathName());
                    any = true;
                    filled++;
                }
                if (!any) {
                    StructureDataType fresh = new StructureDataType(new CategoryPath(parts[0].isEmpty() ? "/" : parts[0]), parts[1], 0, dtm);
                    Structure added = (Structure) dtm.addDataType(fresh, DataTypeConflictHandler.REPLACE_HANDLER);
                    fill(added, t);
                    changed.add(added.getPathName());
                    created++;
                }
            } catch (Exception e) {
                failed++;
            }
        }

        // The functions that say so in their signature. Asking each function is the whole cost of
        // this script on a large program, so it is skipped when nothing was filled.
        TreeSet<Long> touched = new TreeSet<>();
        if (!changed.isEmpty()) {
            Address imageBase = currentProgram.getImageBase();
            for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
                if (monitor.isCancelled()) {
                    break;
                }
                boolean uses = false;
                DataType returned = base(f.getReturnType());
                if (returned != null && changed.contains(returned.getPathName())) {
                    uses = true;
                }
                if (!uses) {
                    for (Parameter p : f.getParameters()) {
                        DataType d = base(p.getDataType());
                        if (d != null && changed.contains(d.getPathName())) {
                            uses = true;
                            break;
                        }
                    }
                }
                if (uses) {
                    touched.add(f.getEntryPoint().subtract(imageBase));
                }
            }
        }
        try (BufferedWriter w = new BufferedWriter(new OutputStreamWriter(new FileOutputStream(args[1], true), "UTF-8"))) {
            for (long rva : touched) {
                w.write(Long.toHexString(rva).toUpperCase());
                w.write('\n');
            }
        }
        println("ApplyTypes: " + filled + " structures filled, " + created + " created, " + failed + " failed, "
                + touched.size() + " functions have one in their signature");
        println("gatekit-script: ApplyTypes done filled=" + filled + " created=" + created + " failed=" + failed + " touched=" + touched.size());
    }
}
