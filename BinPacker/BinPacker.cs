using System;
using System.Collections.Generic;
using System.IO;

namespace BinPacker
{
    internal class BinPacker
    {
        public static void Pack(string dirPath, string binPath, string hdPath)
        {
            BinaryWriter binWriter = new BinaryWriter(File.Create(binPath));
            BinaryWriter hdWriter = new BinaryWriter(File.Create(hdPath));
            foreach (string file in Directory.GetFiles(dirPath))
            {
                BinaryReader reader = new BinaryReader(File.OpenRead(file));
                byte[] data = reader.ReadBytes((int)reader.BaseStream.Length);
                data = Encrypt(data, out int finalSize);
                binWriter.Write(data);
                hdWriter.Write(BitConverter.GetBytes(finalSize));
            }
            binWriter.Close();
            hdWriter.Close();
        }

        static byte[] Encrypt(byte[] data, out int finalSize, int nSegments = 1)
        {
            List<byte> temp2 = new List<byte>();
            List<byte> temp1 = new List<byte>();
            int nSteps = data.Length / nSegments;
            int loopRange = nSteps * nSegments;
            for (int i = 0; i < nSegments; i++)
            {
                for (int j = i; j < loopRange; j += nSegments)
                {
                    temp1.Add(data[j]);
                }
            }
            for (int i = loopRange; i < data.Length; i++)
            {
                temp1.Add(data[i]);
            }
            for (int i = 0; i < temp1.Count; i += 0x20)
            {
                int left = temp1.Count - i;
                int length = left > 0x20 ? 0x20 : left;
                temp2.Add((byte)(length - 1 | 0xE0));
                for (int j = i; j < i + length; j++)
                    temp2.Add(temp1[j]);
            }
            finalSize = temp2.Count + 8;
            byte[] result = new byte[(finalSize + 0xF) & ~0xF];
            BitConverter.GetBytes(data.Length).CopyTo(result, 0);
            BitConverter.GetBytes(nSegments).CopyTo(result, 4);
            temp2.CopyTo(result, 8);
            for (int i = finalSize; i < result.Length; i++)
                result[i] = 0xFF;
            return result;
        }
    }
}
