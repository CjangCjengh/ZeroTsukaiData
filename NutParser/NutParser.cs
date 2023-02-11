using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NutParser
{
    internal class NutParser
    {
        static readonly Dictionary<byte, string> OpcodeNames = new Dictionary<byte, string>()
        {
            {0x00, "_OP_LINE"},
            {0x01, "_OP_LOAD"},
            {0x02, "_OP_LOADINT"},
            {0x03, "_OP_LOADFLOAT"},
            {0x04, "_OP_DLOAD"},
            {0x05, "_OP_TAILCALL"},
            {0x06, "_OP_CALL"},
            {0x07, "_OP_PREPCALL"},
            {0x08, "_OP_PREPCALLK"},
            {0x09, "_OP_GETK"},
            {0x0A, "_OP_MOVE"},
            {0x0B, "_OP_NEWSLOT"},
            {0x0C, "_OP_DELETE"},
            {0x0D, "_OP_SET"},
            {0x0E, "_OP_GET"},
            {0x0F, "_OP_EQ"},
            {0x10, "_OP_NE"},
            {0x11, "_OP_ADD"},
            {0x12, "_OP_SUB"},
            {0x13, "_OP_MUL"},
            {0x14, "_OP_DIV"},
            {0x15, "_OP_MOD"},
            {0x16, "_OP_BITW"},
            {0x17, "_OP_RETURN"},
            {0x18, "_OP_LOADNULLS"},
            {0x19, "_OP_LOADROOT"},
            {0x1A, "_OP_LOADBOOL"},
            {0x1B, "_OP_DMOVE"},
            {0x1C, "_OP_JMP"},
            {0x1D, "_OP_JCMP"},
            {0x1E, "_OP_JZ"},
            {0x1F, "_OP_SETOUTER"},
            {0x20, "_OP_GETOUTER"},
            {0x21, "_OP_NEWOBJ"},
            {0x22, "_OP_APPENDARRAY"},
            {0x23, "_OP_COMPARITH"},
            {0x24, "_OP_INC"},
            {0x25, "_OP_INCL"},
            {0x26, "_OP_PINC"},
            {0x27, "_OP_PINCL"},
            {0x28, "_OP_CMP"},
            {0x29, "_OP_EXISTS"},
            {0x2A, "_OP_INSTANCEOF"},
            {0x2B, "_OP_AND"},
            {0x2C, "_OP_OR"},
            {0x2D, "_OP_NEG"},
            {0x2E, "_OP_NOT"},
            {0x2F, "_OP_BWNOT"},
            {0x30, "_OP_CLOSURE"},
            {0x31, "_OP_YIELD"},
            {0x32, "_OP_RESUME"},
            {0x33, "_OP_FOREACH"},
            {0x34, "_OP_POSTFOREACH"},
            {0x35, "_OP_CLONE"},
            {0x36, "_OP_TYPEOF"},
            {0x37, "_OP_PUSHTRAP"},
            {0x38, "_OP_POPTRAP"},
            {0x39, "_OP_THROW"},
            {0x3A, "_OP_NEWSLOTA"},
            {0x3B, "_OP_GETBASE"},
            {0x3C, "_OP_CLOSE"}
        };

        static readonly List<byte> StringOpcodes = new List<byte>()
        {
            0x01, 0x04
        };

        static int[] FindAll(byte[] data, byte[] key, int start = 0, int? end = null)
        {
            if (end == null) end = data.Length;
            List<int> indexes = new List<int>();
            for (int i = start; i <= end - key.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < key.Length; j++)
                {
                    if (data[i + j] != key[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (!match) continue;
                indexes.Add(i);
            }
            return indexes.ToArray();
        }

        static int ReadInt(byte[] bytes, int index = 0)
        {
            return (bytes[index + 3] << 24) | (bytes[index + 2] << 16) |
                    (bytes[index + 1] << 8) | bytes[index];
        }

        public static void Parse(string filePath, string outPath)
        {
            BinaryReader reader = new BinaryReader(File.OpenRead(filePath));
            byte[] data = reader.ReadBytes((int)reader.BaseStream.Length);
            int[] boundaries = FindAll(data, Encoding.ASCII.GetBytes("TRAP"));
            List<string> strings = new List<string>();
            foreach (int index in FindAll(data,
                new byte[] { 0x10, 0x00, 0x00, 0x08 },
                boundaries[2] + 4, boundaries[3]))
            {
                int length = ReadInt(data, index + 4);
                strings.Add(Encoding.GetEncoding(932).GetString(data, index + 8, length));
            }

            List<string> instructions = new List<string>();
            for (int index = boundaries[7] + 4; index < boundaries[8]; index += 8)
            {
                int oprand = ReadInt(data, index);
                byte opcode = data[index + 4];
                int arg3 = data[index + 7];
                if (StringOpcodes.Contains(opcode))
                {
                    instructions.Add($"{OpcodeNames[opcode].Substring(4)} {strings[oprand]} {strings[arg3]}");
                }
                else
                {
                    instructions.Add($"{OpcodeNames[opcode].Substring(4)} {oprand}");
                }
            }

            StreamWriter writer = new StreamWriter(outPath);
            instructions.ForEach(s => writer.WriteLine(s));
            writer.Close();
        }
    }
}
