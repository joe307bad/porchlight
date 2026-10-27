// A reflection-driven port of @colyseus/schema's decoder (v4): the room sends
// its type definitions in the join handshake, then the full state and patches
// to it. Nothing here knows the server's classes: a struct is a SchemaObj with
// values by field index, a map a SchemaMap (insertion ordered, like a JS Map),
// an array a SchemaArray. Read them with SchemaObj.Get(name).

using System;
using System.Collections.Generic;
using System.Text;

namespace Porchlight.Net
{
    public sealed class SchemaField
    {
        public string name;
        /// <summary>"ref", "map", "array", ... for containers; otherwise the primitive ("string", "float32", ...).</summary>
        public string kind;
        /// <summary>A collection's primitive children ("map:string"), or null.</summary>
        public string childPrimitive;
        public SchemaType refType;
    }

    public sealed class SchemaType
    {
        public int id;
        public readonly List<SchemaField> fields = new List<SchemaField>();
        public string name;
    }

    public interface ISchemaRef
    {
        object GetByIndex(int index);
        void DeleteByIndex(int index);
    }

    public sealed class SchemaObj : ISchemaRef
    {
        public readonly SchemaType type;
        public readonly object[] values;
        public int refId = -1;

        public SchemaObj(SchemaType type)
        {
            this.type = type;
            values = new object[Math.Max(type.fields.Count, 1)];
        }

        public object GetByIndex(int index) => index < values.Length ? values[index] : null;
        public void DeleteByIndex(int index) { if (index < values.Length) values[index] = null; }

        public object Get(string name)
        {
            for (int i = 0; i < type.fields.Count; i++) if (type.fields[i].name == name) return values[i];
            return null;
        }

        public string Str(string name) => Get(name) as string;
        public double Num(string name) => Get(name) is double d ? d : 0;
        public bool Bool(string name) => Get(name) is bool b && b;
    }

    public abstract class SchemaCollection : ISchemaRef
    {
        public SchemaType childType;
        public string childPrimitive;
        public int refId = -1;

        public abstract object GetByIndex(int index);
        public abstract void DeleteByIndex(int index);
        public abstract void Clear();
        public abstract IEnumerable<object> Values();
        public abstract SchemaCollection CloneEmpty();
    }

    public sealed class SchemaMap : SchemaCollection
    {
        /// <summary>Keys in insertion order.</summary>
        public readonly List<string> keys = new List<string>();
        public readonly Dictionary<string, object> items = new Dictionary<string, object>();
        readonly Dictionary<int, string> indexes = new Dictionary<int, string>();

        public void SetIndex(int index, string key) => indexes[index] = key;
        public string GetIndex(int index) => indexes.TryGetValue(index, out var k) ? k : null;

        public void Set(string key, object value)
        {
            if (!items.ContainsKey(key)) keys.Add(key);
            items[key] = value;
        }

        public override object GetByIndex(int index)
        {
            var k = GetIndex(index);
            return k != null && items.TryGetValue(k, out var v) ? v : null;
        }

        public override void DeleteByIndex(int index)
        {
            var k = GetIndex(index);
            if (k == null) return;
            if (items.Remove(k)) keys.Remove(k);
            indexes.Remove(index);
        }

        public override void Clear()
        {
            keys.Clear();
            items.Clear();
            indexes.Clear();
        }

        public override IEnumerable<object> Values()
        {
            foreach (var k in keys) yield return items[k];
        }

        public override SchemaCollection CloneEmpty() => new SchemaMap();
    }

    public sealed class SchemaArray : SchemaCollection
    {
        public readonly List<object> items = new List<object>();

        public void SetAt(int index, object value)
        {
            while (items.Count <= index) items.Add(null);
            items[index] = value;
        }

        public override object GetByIndex(int index) => index >= 0 && index < items.Count ? items[index] : null;

        public override void DeleteByIndex(int index)
        {
            if (index >= 0 && index < items.Count) items.RemoveAt(index);
        }

        public override void Clear() => items.Clear();

        public override IEnumerable<object> Values()
        {
            foreach (var v in items) if (v != null) yield return v;
        }

        public override SchemaCollection CloneEmpty() => new SchemaArray();
    }

    /// <summary>The schema wire format's numbers and strings (little-endian, msgpack-like prefixes).</summary>
    public static class SchemaWire
    {
        public const int SWITCH_TO_STRUCTURE = 255;
        public const int TYPE_ID = 213;
        public const int OP_ADD = 128;
        public const int OP_REPLACE = 0;
        public const int OP_DELETE = 64;
        public const int OP_DELETE_AND_ADD = 192;
        public const int OP_CLEAR = 10;
        public const int OP_REVERSE = 15;
        public const int OP_DELETE_BY_REFID = 33;
        public const int OP_ADD_BY_REFID = 129;

        public static int Int32(byte[] b, ref int i)
        {
            int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
            i += 4;
            return v;
        }

        public static uint UInt32(byte[] b, ref int i) => (uint)Int32(b, ref i);

        public static int UInt16(byte[] b, ref int i)
        {
            int v = b[i] | (b[i + 1] << 8);
            i += 2;
            return v;
        }

        public static float Float32(byte[] b, ref int i)
        {
            var t = new byte[4];
            Buffer.BlockCopy(b, i, t, 0, 4);
            i += 4;
            if (!BitConverter.IsLittleEndian) Array.Reverse(t);
            return BitConverter.ToSingle(t, 0);
        }

        public static double Float64(byte[] b, ref int i)
        {
            var t = new byte[8];
            Buffer.BlockCopy(b, i, t, 0, 8);
            i += 8;
            if (!BitConverter.IsLittleEndian) Array.Reverse(t);
            return BitConverter.ToDouble(t, 0);
        }

        public static string String(byte[] b, ref int i)
        {
            int prefix = b[i++];
            int length = 0;
            if (prefix < 0xc0) length = prefix & 0x1f;
            else if (prefix == 0xd9) length = b[i++];
            else if (prefix == 0xda) length = UInt16(b, ref i);
            else if (prefix == 0xdb) length = (int)UInt32(b, ref i);
            if (length > b.Length - i) length = b.Length - i;
            var s = Encoding.UTF8.GetString(b, i, length);
            i += length;
            return s;
        }

        public static double Number(byte[] b, ref int i)
        {
            int prefix = b[i++];
            if (prefix < 0x80) return prefix;
            switch (prefix)
            {
                case 0xca: return Float32(b, ref i);
                case 0xcb: return Float64(b, ref i);
                case 0xcc: return b[i++];
                case 0xcd: return UInt16(b, ref i);
                case 0xce: return UInt32(b, ref i);
                case 0xcf:
                {
                    double low = UInt32(b, ref i);
                    return UInt32(b, ref i) * 4294967296.0 + low;
                }
                case 0xd0: return (sbyte)b[i++];
                case 0xd1: return (short)UInt16(b, ref i);
                case 0xd2: return Int32(b, ref i);
                case 0xd3:
                {
                    double low = UInt32(b, ref i);
                    return Int32(b, ref i) * 4294967296.0 + low;
                }
            }
            if (prefix > 0xdf) return prefix - 256;
            return double.NaN;
        }

        public static bool StringCheck(byte[] b, int i)
        {
            int p = b[i];
            return (p < 0xc0 && p > 0xa0) || p == 0xd9 || p == 0xda || p == 0xdb;
        }

        public static object Primitive(string type, byte[] b, ref int i)
        {
            switch (type)
            {
                case "string": return String(b, ref i);
                case "number": return Number(b, ref i);
                case "boolean": return b[i++] > 0;
                case "int8": return (double)(sbyte)b[i++];
                case "uint8": return (double)b[i++];
                case "int16": return (double)(short)UInt16(b, ref i);
                case "uint16": return (double)UInt16(b, ref i);
                case "int32": return (double)Int32(b, ref i);
                case "uint32": return (double)UInt32(b, ref i);
                case "float32": return (double)Float32(b, ref i);
                case "float64": return Float64(b, ref i);
                case "int64":
                {
                    double low = UInt32(b, ref i);
                    return Int32(b, ref i) * 4294967296.0 + low;
                }
                case "uint64":
                {
                    double low = UInt32(b, ref i);
                    return UInt32(b, ref i) * 4294967296.0 + low;
                }
                default: throw new FormatException("schema: unknown primitive " + type);
            }
        }

        /// <summary>Schema's encode.string, used for message types.</summary>
        public static void WriteString(List<byte> o, string s) => MsgPack.WriteString(o, s);
    }

    public sealed class SchemaDecoder
    {
        public readonly Dictionary<int, SchemaType> types;
        public readonly SchemaObj state;
        readonly Dictionary<int, ISchemaRef> refs = new Dictionary<int, ISchemaRef>();
        readonly Dictionary<int, int> refCount = new Dictionary<int, int>();
        readonly List<int> deletedRefs = new List<int>();

        public SchemaDecoder(SchemaType root, Dictionary<int, SchemaType> types)
        {
            this.types = types;
            state = new SchemaObj(root);
            AddRef(0, state, true);
        }

        void SetRefId(ISchemaRef r, int id)
        {
            if (r is SchemaObj o) o.refId = id;
            else if (r is SchemaCollection c) c.refId = id;
        }

        static int RefIdOf(object r) => r is SchemaObj o ? o.refId : r is SchemaCollection c ? c.refId : -1;

        void AddRef(int refId, ISchemaRef r, bool increment)
        {
            refs[refId] = r;
            SetRefId(r, refId);
            if (increment) refCount[refId] = (refCount.TryGetValue(refId, out var n) ? n : 0) + 1;
            deletedRefs.Remove(refId);
        }

        void RemoveRef(int refId)
        {
            if (!refCount.TryGetValue(refId, out var n) || n == 0) return;
            refCount[refId] = n - 1;
            if (n - 1 <= 0 && !deletedRefs.Contains(refId)) deletedRefs.Add(refId);
        }

        void GarbageCollect()
        {
            for (int k = 0; k < deletedRefs.Count; k++)
            {
                int refId = deletedRefs[k];
                if (refCount.TryGetValue(refId, out var n) && n > 0) continue;
                if (!refs.TryGetValue(refId, out var r)) continue;
                if (r is SchemaObj o)
                {
                    foreach (var child in o.values)
                    {
                        int cid = RefIdOf(child);
                        if (cid >= 0 && !deletedRefs.Contains(cid)) RemoveRef(cid);
                    }
                }
                else if (r is SchemaCollection c && c.childType != null)
                {
                    foreach (var child in c.Values())
                    {
                        int cid = RefIdOf(child);
                        if (cid >= 0 && !deletedRefs.Contains(cid)) RemoveRef(cid);
                    }
                }
                refs.Remove(refId);
                refCount.Remove(refId);
            }
            deletedRefs.Clear();
        }

        public void Decode(byte[] b, int offset)
        {
            int i = offset;
            ISchemaRef r = state;
            while (i < b.Length)
            {
                if (b[i] == SchemaWire.SWITCH_TO_STRUCTURE)
                {
                    i++;
                    int next = (int)SchemaWire.Number(b, ref i);
                    if (refs.TryGetValue(next, out var nr)) r = nr;
                    else SkipStructure(b, ref i);
                    continue;
                }
                bool ok = r switch
                {
                    SchemaObj o => DecodeSchema(b, ref i, o),
                    SchemaMap m => DecodeMap(b, ref i, m),
                    SchemaArray a => DecodeArray(b, ref i, a),
                    _ => false,
                };
                if (!ok) SkipStructure(b, ref i);
            }
            GarbageCollect();
        }

        void SkipStructure(byte[] b, ref int i)
        {
            while (i < b.Length)
            {
                if (b[i] == SchemaWire.SWITCH_TO_STRUCTURE)
                {
                    int j = i + 1;
                    if (refs.ContainsKey((int)SchemaWire.Number(b, ref j))) break;
                }
                i++;
            }
        }

        SchemaType InstanceType(byte[] b, ref int i, SchemaType fallback)
        {
            if (b[i] == SchemaWire.TYPE_ID)
            {
                i++;
                int id = (int)SchemaWire.Number(b, ref i);
                if (types.TryGetValue(id, out var t)) return t;
            }
            return fallback;
        }

        object DecodeValue(int op, ISchemaRef r, int index, SchemaField f, SchemaType childType, string childPrimitive, string kind, byte[] b, ref int i)
        {
            var previous = r.GetByIndex(index);
            object value = null;
            if ((op & SchemaWire.OP_DELETE) == SchemaWire.OP_DELETE)
            {
                int prevId = RefIdOf(previous);
                if (prevId >= 0) RemoveRef(prevId);
                if (op != SchemaWire.OP_DELETE_AND_ADD) r.DeleteByIndex(index);
            }
            if (op == SchemaWire.OP_DELETE) return null;

            if (kind == "ref")
            {
                int refId = (int)SchemaWire.Number(b, ref i);
                refs.TryGetValue(refId, out var existing);
                value = existing;
                if ((op & SchemaWire.OP_ADD) == SchemaWire.OP_ADD)
                {
                    var t = InstanceType(b, ref i, childType);
                    if (value == null) value = new SchemaObj(t);
                    AddRef(refId, (ISchemaRef)value, value != previous || (op == SchemaWire.OP_DELETE_AND_ADD && value == previous));
                }
            }
            else if (kind == "primitive")
            {
                value = SchemaWire.Primitive(childPrimitive, b, ref i);
            }
            else
            {
                int refId = (int)SchemaWire.Number(b, ref i);
                SchemaCollection valueRef;
                if (refs.TryGetValue(refId, out var known)) valueRef = (previous as SchemaCollection) ?? (SchemaCollection)known;
                else valueRef = kind == "map" ? new SchemaMap() : (SchemaCollection)new SchemaArray();
                // The JS client clones the collection here (keeping its items);
                // reusing the same object is equivalent for a reader.
                valueRef.childType = f?.refType ?? childType;
                valueRef.childPrimitive = f?.childPrimitive ?? childPrimitive;
                if (previous is SchemaCollection pc && pc.refId >= 0 && pc.refId != refId && (op & SchemaWire.OP_DELETE) != SchemaWire.OP_DELETE)
                    RemoveRef(pc.refId);
                AddRef(refId, valueRef, valueRef != previous || (op == SchemaWire.OP_DELETE_AND_ADD && valueRef == previous));
                value = valueRef;
            }
            return value;
        }

        bool DecodeSchema(byte[] b, ref int i, SchemaObj o)
        {
            int first = b[i++];
            int op = (first >> 6) << 6;
            int index = first % (op == 0 ? 255 : op);
            if (index >= o.type.fields.Count) return false;
            var f = o.type.fields[index];
            string kind = f.kind == "ref" ? "ref" : f.kind == "map" || f.kind == "array" ? f.kind : "primitive";
            var value = DecodeValue(op, o, index, f, f.refType, f.kind, kind, b, ref i);
            if (value != null) o.values[index] = value;
            return true;
        }

        bool DecodeMap(byte[] b, ref int i, SchemaMap m)
        {
            int op = b[i++];
            if (op == SchemaWire.OP_CLEAR)
            {
                RemoveChildRefs(m);
                m.Clear();
                return true;
            }
            int index = (int)SchemaWire.Number(b, ref i);
            string key;
            if ((op & SchemaWire.OP_ADD) == SchemaWire.OP_ADD)
            {
                key = SchemaWire.String(b, ref i);
                m.SetIndex(index, key);
            }
            else key = m.GetIndex(index);
            string kind = m.childType != null ? "ref" : "primitive";
            var value = DecodeValue(op, m, index, null, m.childType, m.childPrimitive, kind, b, ref i);
            if (value != null && key != null) m.Set(key, value);
            return true;
        }

        bool DecodeArray(byte[] b, ref int i, SchemaArray a)
        {
            int op = b[i++];
            int index;
            if (op == SchemaWire.OP_CLEAR)
            {
                RemoveChildRefs(a);
                a.Clear();
                return true;
            }
            if (op == SchemaWire.OP_REVERSE)
            {
                a.items.Reverse();
                return true;
            }
            if (op == SchemaWire.OP_DELETE_BY_REFID)
            {
                int rid = (int)SchemaWire.Number(b, ref i);
                refs.TryGetValue(rid, out var prev);
                index = a.items.IndexOf(prev);
                a.DeleteByIndex(index);
                return true;
            }
            if (op == SchemaWire.OP_ADD_BY_REFID)
            {
                int rid = (int)SchemaWire.Number(b, ref i);
                index = -1;
                if (refs.TryGetValue(rid, out var item)) index = a.items.IndexOf(item);
                if (index == -1) index = a.items.Count;
            }
            else index = (int)SchemaWire.Number(b, ref i);
            string kind = a.childType != null ? "ref" : "primitive";
            var previous = a.GetByIndex(index);
            var value = DecodeValue(op, a, index, null, a.childType, a.childPrimitive, kind, b, ref i);
            if (value != null && value != previous) a.SetAt(index, value);
            return true;
        }

        void RemoveChildRefs(SchemaCollection c)
        {
            if (c.childType == null) return;
            foreach (var v in c.Values())
            {
                int id = RefIdOf(v);
                if (id >= 0) RemoveRef(id);
            }
        }
    }

    /// <summary>Reads the type definitions a room sends in its join handshake.</summary>
    public static class Reflection
    {
        static SchemaField F(string name, string kind, SchemaType refType = null) => new SchemaField { name = name, kind = kind, refType = refType };

        public static SchemaDecoder Decode(byte[] b, int offset)
        {
            // The bootstrap types (Reflection.ts): Reflection { types: [ReflectionType], rootType }.
            var rField = new SchemaType { name = "ReflectionField" };
            rField.fields.Add(F("name", "string"));
            rField.fields.Add(F("type", "string"));
            rField.fields.Add(F("referencedType", "number"));
            var rType = new SchemaType { name = "ReflectionType" };
            rType.fields.Add(F("id", "number"));
            rType.fields.Add(F("extendsId", "number"));
            rType.fields.Add(F("fields", "array", rField));
            var rRoot = new SchemaType { name = "Reflection" };
            rRoot.fields.Add(F("types", "array", rType));
            rRoot.fields.Add(F("rootType", "number"));

            var boot = new SchemaDecoder(rRoot, new Dictionary<int, SchemaType>());
            boot.Decode(b, offset);

            var reflected = new List<SchemaObj>();
            if (boot.state.Get("types") is SchemaArray ta)
                foreach (var v in ta.Values()) reflected.Add((SchemaObj)v);

            var types = new Dictionary<int, SchemaType>();
            foreach (var rt in reflected)
            {
                int id = (int)rt.Num("id");
                types[id] = new SchemaType { id = id };
            }
            foreach (var rt in reflected)
            {
                var t = types[(int)rt.Num("id")];
                // Inherited fields come first, the base type's own before its children's.
                var chain = new List<SchemaObj>();
                var cur = rt;
                while (cur != null)
                {
                    chain.Add(cur);
                    var ext = cur.Get("extendsId");
                    SchemaObj parent = null;
                    if (ext is double e)
                        foreach (var cand in reflected)
                            if (cand.Num("id") == e) { parent = cand; break; }
                    cur = parent;
                }
                chain.Reverse();
                foreach (var link in chain)
                {
                    if (!(link.Get("fields") is SchemaArray fa)) continue;
                    foreach (var fv in fa.Values())
                    {
                        var rf = (SchemaObj)fv;
                        string name = rf.Str("name");
                        string type = rf.Str("type");
                        var refd = rf.Get("referencedType");
                        var field = new SchemaField { name = name, kind = type };
                        if (refd is double rd)
                        {
                            if (types.TryGetValue((int)rd, out var rtType)) field.refType = rtType;
                            else
                            {
                                var parts = type.Split(':');
                                field.kind = parts[0];
                                field.childPrimitive = parts.Length > 1 ? parts[1] : null;
                            }
                        }
                        t.fields.Add(field);
                    }
                }
            }
            int root = boot.state.Get("rootType") is double r ? (int)r : 0;
            if (!types.TryGetValue(root, out var rootType)) rootType = new SchemaType();
            return new SchemaDecoder(rootType, types);
        }
    }
}
