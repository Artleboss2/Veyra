using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Veyra
{
    internal class AxmlAttr
    {
        public string Namespace = "";
        public string Name = "";
        public int ResId;
        public int Type;          // Res_value dataType
        public int Data;          // raw integer payload / string index
        public string Raw = "";   // resolved textual value

        public int IntValue
        {
            get
            {
                if (Type == 0x12) return Data != 0 ? 1 : 0; // boolean
                return Data;
            }
        }

        public string StringValue { get { return Raw; } }
    }

    internal class AxmlElement
    {
        public string Name = "";
        public string Namespace = "";
        public int Line;
        public List<AxmlAttr> Attributes = new List<AxmlAttr>();
        public List<AxmlElement> Children = new List<AxmlElement>();
        public AxmlElement Parent;

        public AxmlAttr Attr(string name)
        {
            for (int i = 0; i < Attributes.Count; i++)
                if (string.Equals(Attributes[i].Name, name, StringComparison.Ordinal)) return Attributes[i];
            return null;
        }

        public AxmlAttr Attr(int resId)
        {
            for (int i = 0; i < Attributes.Count; i++)
                if (Attributes[i].ResId == resId) return Attributes[i];
            return null;
        }

        public string AttrText(string name)
        {
            AxmlAttr a = Attr(name);
            return a == null ? "" : a.Raw;
        }

        public int AttrInt(string name, int fallback)
        {
            AxmlAttr a = Attr(name);
            if (a == null) return fallback;
            if (a.Type == 0x12) return a.Data != 0 ? 1 : 0;
            return a.Data;
        }

        public IEnumerable<AxmlElement> All(string name)
        {
            for (int i = 0; i < Children.Count; i++)
                if (string.Equals(Children[i].Name, name, StringComparison.Ordinal))
                    yield return Children[i];
        }
    }

    /// <summary>Android binary XML (AXML) parser \u2014 reads compiled AndroidManifest.xml directly.</summary>
    internal class Axml
    {
        private readonly byte[] data;
        private readonly List<string> strings = new List<string>();
        private readonly List<int> resIds = new List<int>();
        public AxmlElement Root;

        // Attribute resource IDs from the Android framework.
        public const int ATTR_LABEL = 0x01010001;
        public const int ATTR_ICON = 0x01010002;
        public const int ATTR_NAME = 0x01010003;
        public const int ATTR_VERSION_CODE = 0x0101021b;
        public const int ATTR_VERSION_NAME = 0x0101021c;
        public const int ATTR_MIN_SDK = 0x0101020c;
        public const int ATTR_TARGET_SDK = 0x01010270;
        public const int ATTR_DEBUGGABLE = 0x0101000f;
        public const int ATTR_EXPORTED = 0x01010010;
        public const int ATTR_REQUIRED = 0x0101028e;
        public const int ATTR_COMPILE_SDK = 0x01010572;

        public Axml(byte[] bytes)
        {
            data = bytes;
            Parse();
        }

        private ushort U16(int o) { return (ushort)(data[o] | (data[o + 1] << 8)); }
        private uint U32(int o) { return (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24)); }

        private void Parse()
        {
            if (data.Length < 16) throw new InvalidDataException("Manifest too small");
            ushort type = U16(0);
            if (type != 0x0003) throw new InvalidDataException("Not a binary XML document (chunk 0x" + type.ToString("x4") + ")");

            int headerSize = U16(2);
            int total = (int)Math.Min((uint)data.Length, U32(4));
            int off = headerSize;

            Stack<AxmlElement> stack = new Stack<AxmlElement>();

            while (off + 8 <= total)
            {
                ushort ctype = U16(off);
                ushort chdr = U16(off + 2);
                int csize = (int)U32(off + 4);
                if (csize < 8 || off + csize > total) break;

                if (ctype == 0x0001) ReadStringPool(off);
                else if (ctype == 0x0180) ReadResourceMap(off, csize);
                else if (ctype == 0x0102)
                {
                    AxmlElement el = ReadStartElement(off, chdr);
                    if (stack.Count > 0)
                    {
                        el.Parent = stack.Peek();
                        el.Parent.Children.Add(el);
                    }
                    else if (Root == null) Root = el;
                    stack.Push(el);
                }
                else if (ctype == 0x0103)
                {
                    if (stack.Count > 0) stack.Pop();
                }

                off += csize;
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
            if (count < 0 || count > 500000) return;
            if (offsetsBase + count * 4 > data.Length) return;

            for (int i = 0; i < count; i++)
            {
                int so = dataBase + (int)U32(offsetsBase + i * 4);
                if (so < 0 || so >= data.Length) { strings.Add(""); continue; }
                try { strings.Add(utf8 ? ReadUtf8(so) : ReadUtf16(so)); }
                catch { strings.Add(""); }
            }
        }

        private string ReadUtf8(int at)
        {
            int p = at;
            ReadVarint(p, out p);            // utf16 length
            int len = ReadVarint(p, out p);  // byte length
            if (len < 0 || p + len > data.Length) return "";
            return Encoding.UTF8.GetString(data, p, len);
        }

        private int ReadVarint(int p, out int next)
        {
            int value = 0;
            if (p >= data.Length) { next = p; return 0; }
            byte b = data[p++];
            if ((b & 0x80) != 0)
            {
                value = ((b & 0x7F) << 8) | data[p++];
                if ((value & 0x8000) != 0) value = ((value & 0x7FFF) << 8) | data[p++];
            }
            else value = b;
            next = p;
            return value;
        }

        private string ReadUtf16(int at)
        {
            int p = at;
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

        private void ReadResourceMap(int chunk, int size)
        {
            int n = (size - 8) / 4;
            for (int i = 0; i < n; i++) resIds.Add((int)U32(chunk + 8 + i * 4));
        }

        private string Str(int idx)
        {
            if (idx < 0 || idx >= strings.Count) return "";
            return strings[idx];
        }

        private AxmlElement ReadStartElement(int chunk, int headerSize)
        {
            AxmlElement el = new AxmlElement();
            el.Line = (int)U32(chunk + 8);
            int nsIdx = (int)U32(chunk + 16);
            int nameIdx = (int)U32(chunk + 20);
            el.Namespace = Str(nsIdx);
            el.Name = Str(nameIdx);

            int attrStart = U16(chunk + 24);
            int attrSize = U16(chunk + 26);
            int attrCount = U16(chunk + 28);
            if (attrSize < 20) attrSize = 20;

            int baseOff = chunk + headerSize + attrStart;
            for (int i = 0; i < attrCount; i++)
            {
                int o = baseOff + i * attrSize;
                if (o + 20 > data.Length) break;
                AxmlAttr a = new AxmlAttr();
                int ans = (int)U32(o);
                int aname = (int)U32(o + 4);
                int araw = (int)U32(o + 8);
                a.Namespace = Str(ans);
                a.Name = Str(aname);
                a.Type = data[o + 15];
                a.Data = (int)U32(o + 16);

                if (aname >= 0 && aname < resIds.Count) a.ResId = resIds[aname];

                if (araw != -1) a.Raw = Str(araw);
                else a.Raw = FormatValue(a.Type, a.Data);
                el.Attributes.Add(a);
            }
            return el;
        }

        private string FormatValue(int type, int data)
        {
            switch (type)
            {
                case 0x00: return "";
                case 0x01: return "@0x" + ((uint)data).ToString("x8");
                case 0x02: return "?0x" + ((uint)data).ToString("x8");
                case 0x03: return Str(data);
                case 0x04:
                    byte[] fb = BitConverter.GetBytes(data);
                    return BitConverter.ToSingle(fb, 0).ToString("0.###");
                case 0x05: return (data >> 8) + "dim";
                case 0x06: return (data >> 8) + "%";
                case 0x10: return data.ToString();
                case 0x11: return "0x" + ((uint)data).ToString("x");
                case 0x12: return data != 0 ? "true" : "false";
                case 0x1c:
                case 0x1d:
                case 0x1e:
                case 0x1f: return "#" + ((uint)data).ToString("x8");
                default: return data.ToString();
            }
        }

        public IEnumerable<AxmlElement> Walk()
        {
            if (Root == null) yield break;
            Stack<AxmlElement> s = new Stack<AxmlElement>();
            s.Push(Root);
            while (s.Count > 0)
            {
                AxmlElement e = s.Pop();
                yield return e;
                for (int i = e.Children.Count - 1; i >= 0; i--) s.Push(e.Children[i]);
            }
        }
    }
}
