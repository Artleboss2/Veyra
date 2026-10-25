using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Veyra
{
    internal class ResVariant
    {
        public int DataType;
        public int Data;
        public int Density;
        public bool DefaultLocale;
        public string Str;
    }

    internal class ResType
    {
        public string Name = "";
        public Dictionary<int, List<ResVariant>> Entries = new Dictionary<int, List<ResVariant>>();
    }

    /// <summary>resources.arsc reader: resolves resource IDs to strings and file paths (labels, icons).</summary>
    internal class Arsc
    {
        private byte[] data;
        private List<string> strings = new List<string>();
        private Dictionary<int, ResType> types = new Dictionary<int, ResType>();
        public int TypeCount { get { return types.Count; } }
        public int StringCount { get { return strings.Count; } }

        private ushort U16(int o) { return (ushort)(data[o] | (data[o + 1] << 8)); }
        private uint U32(int o) { return (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24)); }

        public Arsc(byte[] bytes)
        {
            data = bytes;
            if (bytes.Length < 12 || U16(0) != 0x0002) throw new InvalidDataException("Not a resource table");
            ParseTable();
        }

        private void ParseTable()
        {
            int off = U16(2);
            int total = Math.Min(data.Length, (int)U32(4));
            while (off + 8 <= total)
            {
                ushort t = U16(off);
                int size = (int)U32(off + 4);
                if (size < 8 || off + size > total) break;
                if (t == 0x0001) ReadStringPool(off);
                else if (t == 0x0200) ReadPackage(off, size);
                off += size;
            }
        }

        private void ReadStringPool(int chunk)
        {
            int count = (int)U32(chunk + 8);
            int flags = (int)U32(chunk + 16);
            int stringsStart = (int)U32(chunk + 20);
            bool utf8 = (flags & 0x100) != 0;
            int offsetsBase = chunk + 28;
            int dataBase = chunk + stringsStart;
            if (count <= 0 || count > 400000) return;
            if (offsetsBase + count * 4 > data.Length) return;
            strings = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    int p = dataBase + (int)U32(offsetsBase + i * 4);
                    if (p <= 0 || p >= data.Length) { strings.Add(""); continue; }
                    strings.Add(utf8 ? ReadUtf8(p) : ReadUtf16(p));
                }
                catch { strings.Add(""); }
            }
        }

        private string ReadUtf8(int p)
        {
            int len;
            p = Varint(p, out len);       // utf16 length, unused
            p = Varint(p, out len);       // byte length
            if (len < 0 || p + len > data.Length) return "";
            return Encoding.UTF8.GetString(data, p, len);
        }

        private int Varint(int p, out int value)
        {
            value = 0;
            if (p >= data.Length) return p;
            byte b = data[p++];
            if ((b & 0x80) != 0)
            {
                value = ((b & 0x7F) << 8) | data[p++];
                if ((value & 0x8000) != 0) value = ((value & 0x7FFF) << 8) | data[p++];
            }
            else value = b;
            return p;
        }

        private string ReadUtf16(int p)
        {
            int len = U16(p);
            p += 2;
            if ((len & 0x8000) != 0)
            {
                len = ((len & 0x7FFF) << 16) | U16(p);
                p += 2;
            }
            if (len < 0 || p + len * 2 > data.Length) return "";
            return Encoding.Unicode.GetString(data, p, len * 2);
        }

        private string PoolString(int idx)
        {
            if (idx < 0 || idx >= strings.Count) return "";
            return strings[idx];
        }

        private void ReadPackage(int chunk, int size)
        {
            int headerSize = U16(chunk + 2);
            int typeStringsOff = (int)U32(chunk + 268);
            int keyStringsOff = (int)U32(chunk + 276);

            List<string> typeNames = new List<string>();
            if (typeStringsOff > 0 && chunk + typeStringsOff + 28 < chunk + size)
                typeNames = ReadPoolNames(chunk + typeStringsOff);

            int p = chunk + headerSize;
            int end = chunk + size;
            while (p + 8 <= end)
            {
                ushort t = U16(p);
                int csize = (int)U32(p + 4);
                if (csize < 8 || p + csize > end) break;
                if (t == 0x0201) ReadType(p, typeNames);
                p += csize;
            }
        }

        private List<string> ReadPoolNames(int chunk)
        {
            List<string> list = new List<string>();
            int count = (int)U32(chunk + 8);
            int flags = (int)U32(chunk + 16);
            int stringsStart = (int)U32(chunk + 20);
            bool utf8 = (flags & 0x100) != 0;
            int offsetsBase = chunk + 28;
            int dataBase = chunk + stringsStart;
            if (count <= 0 || count > 10000) return list;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    int p = dataBase + (int)U32(offsetsBase + i * 4);
                    list.Add(utf8 ? ReadUtf8(p) : ReadUtf16(p));
                }
                catch { list.Add(""); }
            }
            return list;
        }

        private void ReadType(int chunk, List<string> typeNames)
        {
            int headerSize = U16(chunk + 2);
            int typeId = data[chunk + 8];
            int flags = data[chunk + 9];
            int entryCount = (int)U32(chunk + 12);
            int entriesStart = (int)U32(chunk + 16);
            if (entryCount <= 0 || entryCount > 200000) return;

            int config = chunk + 20;
            int configSize = (int)U32(config);
            int density = 0;
            bool defaultLocale = true;
            if (configSize >= 16)
            {
                density = U16(config + 14);
                defaultLocale = data[config + 8] == 0 && data[config + 9] == 0;
            }

            ResType rt;
            if (!types.TryGetValue(typeId, out rt))
            {
                rt = new ResType();
                rt.Name = (typeId - 1) < typeNames.Count && typeId >= 1 ? typeNames[typeId - 1] : ("type" + typeId);
                types[typeId] = rt;
            }

            int offsetsBase = chunk + headerSize;
            int entriesBase = chunk + entriesStart;

            for (int i = 0; i < entryCount; i++)
            {
                int entryIndex = i;
                int entryOff;
                if ((flags & 0x01) != 0)
                {
                    // Sparse: pairs of (u16 index, u16 offset/4)
                    int o = offsetsBase + i * 4;
                    if (o + 4 > data.Length) break;
                    entryIndex = U16(o);
                    entryOff = U16(o + 2) * 4;
                }
                else if ((flags & 0x02) != 0)
                {
                    int o = offsetsBase + i * 2;
                    if (o + 2 > data.Length) break;
                    entryOff = U16(o) * 4;
                }
                else
                {
                    int o = offsetsBase + i * 4;
                    if (o + 4 > data.Length) break;
                    entryOff = (int)U32(o);
                }

                if (entryOff < 0) continue;
                int ep = entriesBase + entryOff;
                if (ep + 8 > data.Length) continue;

                int esize = U16(ep);
                int eflags = U16(ep + 2);
                if ((eflags & 0x0001) != 0) continue; // complex/bag entry: not a simple value

                int vp = ep + esize;
                if (vp + 8 > data.Length) continue;
                int dtype = data[vp + 3];
                int vdata = (int)U32(vp + 4);

                ResVariant v = new ResVariant();
                v.DataType = dtype;
                v.Data = vdata;
                v.Density = density;
                v.DefaultLocale = defaultLocale;
                if (dtype == 0x03) v.Str = PoolString(vdata);
                else v.Str = "";

                List<ResVariant> list;
                if (!rt.Entries.TryGetValue(entryIndex, out list))
                {
                    list = new List<ResVariant>();
                    rt.Entries[entryIndex] = list;
                }
                list.Add(v);
            }
        }

        /// <summary>Best string value for a resource id (default locale preferred).</summary>
        public string ResolveString(int resId)
        {
            ResVariant v = Resolve(resId, false);
            if (v == null) return "";
            if (v.DataType == 0x03) return v.Str;
            return "";
        }

        /// <summary>Best file path for a resource id (highest density preferred).</summary>
        public string ResolveFile(int resId)
        {
            ResVariant v = Resolve(resId, true);
            if (v == null) return "";
            if (v.DataType == 0x03) return v.Str;
            return "";
        }

        private ResVariant Resolve(int resId, bool preferFile)
        {
            int typeId = (resId >> 16) & 0xFF;
            int entryId = resId & 0xFFFF;
            ResType rt;
            if (!types.TryGetValue(typeId, out rt)) return null;
            List<ResVariant> list;
            if (!rt.Entries.TryGetValue(entryId, out list) || list.Count == 0) return null;

            ResVariant best = null;
            for (int i = 0; i < list.Count; i++)
            {
                ResVariant v = list[i];
                if (v.DataType != 0x03) continue;
                if (best == null) { best = v; continue; }
                if (preferFile)
                {
                    if (v.Density > best.Density) best = v;
                }
                else
                {
                    bool bestDefault = best.DefaultLocale && best.Density == 0;
                    bool curDefault = v.DefaultLocale && v.Density == 0;
                    if (curDefault && !bestDefault) best = v;
                }
            }
            return best;
        }

        public bool HasType(int typeId)
        {
            return types.ContainsKey(typeId);
        }
    }
}
