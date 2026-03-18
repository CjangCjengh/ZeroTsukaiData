using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AudioExtractor
{
    /// <summary>
    /// PS-ADPCM Audio Extractor for Zero no Tsukaima PS2 games.
    /// Supports both VOICE_ID.BIN (mono 22050Hz) and SOUND_ID.BIN (stereo, sample rate from header).
    ///
    /// VOICE_ID format:
    ///   - Continuous raw PS-ADPCM stream, mono 22050Hz 16-bit
    ///   - Voice boundaries are marked by flag=1 frames (end-of-voice marker)
    ///   - VOICE_ID.HD slot sizes do NOT align with voice boundaries;
    ///     a single voice may span multiple HD slots, and an HD slot may
    ///     contain the tail of one voice + the head of the next
    ///   - The only reliable way to split voices is scanning for flag=1 frames
    ///   - 0xFF frames are SPU2 alignment padding (skipped during decode)
    ///
    /// SOUND_ID format:
    ///   - 16-byte header: [reserved][sample_rate(e.g.44050)][mode][interleave=4096]
    ///   - SOUND_ID.HD index: BGM bank sizes (universal parser for all games)
    ///   - Each bank is stereo PS-ADPCM with 4096-byte interleaving (L/R/L/R...)
    ///   - After all banks: SE region with mono PS-ADPCM, flag=1 delimited
    /// </summary>
    internal class AudioExtractor
    {
        // Sony VAG ADPCM prediction coefficients (5 sets)
        static readonly double[][] VagCoeff = new double[][]
        {
            new double[] { 0.0, 0.0 },
            new double[] { 60.0 / 64.0, 0.0 },
            new double[] { 115.0 / 64.0, -52.0 / 64.0 },
            new double[] { 98.0 / 64.0, -55.0 / 64.0 },
            new double[] { 122.0 / 64.0, -60.0 / 64.0 },
        };

        #region HD Index

        /// <summary>
        /// Read VOICE_ID.HD index file.
        /// Each uint32 is the allocated size of a voice entry in the BIN
        /// (including padding/silence/FF frames).
        /// Cumulative offsets give exact voice boundaries.
        /// </summary>
        public static uint[] ReadVoiceHD(string hdPath)
        {
            byte[] data = File.ReadAllBytes(hdPath);
            int n = data.Length / 4;
            uint[] sizes = new uint[n];
            for (int i = 0; i < n; i++)
                sizes[i] = BitConverter.ToUInt32(data, i * 4);
            return sizes;
        }

        /// <summary>
        /// Read SOUND_ID.HD index file.
        /// Returns all BGM bank sizes as a flat list.
        ///
        /// HD structure (varies across games):
        ///   Common header: [0][20] (vals[0]=0, vals[1]=20)
        ///   Then: bank sizes separated by delimiter entries (val=0 or val=20)
        ///   concerto: [0][20][31 large][20,20,20][10 small][20][metadata...]
        ///   fantasia: [0][20][50 large][0,20,20,20][49 small][0,20,20,20,0][metadata...]
        ///   symphony: [0][20][50 large][0][bank][0][bank]...[0,0,0,0,0,0][51 small][metadata...]
        ///
        /// The universal approach: skip header (indices 0,1), then collect all
        /// non-delimiter values (where val > 20) as BGM bank sizes.
        /// </summary>
        public static List<uint> ReadSoundHD(string hdPath)
        {
            byte[] data = File.ReadAllBytes(hdPath);
            int n = data.Length / 4;
            uint[] vals = new uint[n];
            for (int i = 0; i < n; i++)
                vals[i] = BitConverter.ToUInt32(data, i * 4);

            // Skip header (vals[0]=0, vals[1]=20), collect all bank sizes
            // Banks have sizes >> 20 (typically hundreds of KB to MB)
            // Delimiters are 0 or 20
            var banks = new List<uint>();
            for (int i = 2; i < n; i++)
            {
                if (vals[i] > 20)
                    banks.Add(vals[i]);
            }
            return banks;
        }

        #endregion

        #region Flag1 Scanning (fallback)

        /// <summary>
        /// Scan all flag=1 frame positions in a BIN file (fallback when HD is unavailable).
        /// </summary>
        public static List<long> ScanFlag1Positions(string binPath)
        {
            var positions = new List<long>();
            byte[] frame = new byte[16];

            using (var fs = File.OpenRead(binPath))
            {
                long pos = 0;
                while (fs.Read(frame, 0, 16) == 16)
                {
                    if (frame[1] == 1 && !IsAllFF(frame))
                    {
                        positions.Add(pos);
                    }
                    pos += 16;
                }
            }

            return positions;
        }

        /// <summary>
        /// Scan flag=1 positions within a byte array region (for SE extraction).
        /// </summary>
        static List<int> ScanFlag1InRegion(byte[] data)
        {
            var positions = new List<int>();
            for (int i = 0; i + 16 <= data.Length; i += 16)
            {
                if (data[i + 1] == 1 && !IsAllFF(data, i))
                    positions.Add(i);
            }
            return positions;
        }

        #endregion

        #region PS-ADPCM Decode

        /// <summary>
        /// Decode a single PS-ADPCM frame (16 bytes -> 28 PCM samples).
        /// </summary>
        static void DecodeVagFrame(byte[] frame, int offset, List<short> samples,
            ref double hist1, ref double hist2)
        {
            int predictNr = Math.Min((frame[offset] >> 4) & 0xF, 4);
            int shiftFactor = frame[offset] & 0xF;
            double coeff1 = VagCoeff[predictNr][0];
            double coeff2 = VagCoeff[predictNr][1];

            for (int i = 2; i < 16; i++)
            {
                byte b = frame[offset + i];
                // Low nibble first, then high nibble
                int[] nibbles = new int[] { b & 0xF, (b >> 4) & 0xF };

                foreach (int rawNibble in nibbles)
                {
                    int nibble = rawNibble;
                    if (nibble >= 8) nibble -= 16; // sign extend 4-bit

                    double sample;
                    if (shiftFactor <= 12)
                        sample = (double)(nibble << (12 - shiftFactor));
                    else
                        sample = (double)(nibble >> (shiftFactor - 12));

                    sample = sample + hist1 * coeff1 + hist2 * coeff2;

                    if (sample > 32767.0) sample = 32767.0;
                    if (sample < -32768.0) sample = -32768.0;

                    hist2 = hist1;
                    hist1 = sample;

                    samples.Add((short)Math.Round(sample));
                }
            }
        }

        /// <summary>
        /// Decode a mono voice/SE chunk of PS-ADPCM data.
        ///
        /// Data layout (from prev voice's flag=1+16 to this voice's flag=1+16):
        ///   Typical: [flag=7] [0x0C silence ×N] [0xFF ×N] [audio frames...] [flag=1]
        ///   No FF:   [flag=7] [0x0C silence ×N] [audio frames...] [flag=1]
        ///
        /// PS2 SPU2 places warm-up silence and alignment padding before each voice.
        /// The key boundary is the 0xFF frames:
        ///   - Everything BEFORE FF = SPU2 padding (skipped)
        ///   - Everything AFTER FF = valid audio (decoded)
        /// If no FF frames exist, only flag=7 frames are skipped.
        /// </summary>
        public static short[] DecodeVoice(byte[] chunk)
        {
            var samples = new List<short>();
            double hist1 = 0.0;
            double hist2 = 0.0;
            int chunkLen = chunk.Length;

            // 扫描FF分界线：找到第一个连续FF区域的结束位置
            int ffEnd = -1;
            for (int i = 0; i + 16 <= chunkLen; i += 16)
            {
                if (IsAllFF(chunk, i))
                {
                    ffEnd = i + 16;
                }
                else if (ffEnd > 0)
                {
                    // FF区域结束后遇到非FF帧，停止搜索
                    break;
                }
            }

            int dataStart;
            if (ffEnd > 0)
            {
                // 有FF分界：从FF之后开始解码
                dataStart = ffEnd;
            }
            else
            {
                // 无FF分界：跳过flag=7帧，从第一个非flag=7帧开始
                dataStart = chunkLen; // 默认：无有效数据
                for (int i = 0; i + 16 <= chunkLen; i += 16)
                {
                    if (chunk[i + 1] == 7)
                        continue;
                    dataStart = i;
                    break;
                }
            }

            // 解码阶段：从dataStart开始，跳过中间穿插的FF帧
            for (int i = dataStart; i + 16 <= chunkLen; i += 16)
            {
                if (IsAllFF(chunk, i))
                    continue;
                DecodeVagFrame(chunk, i, samples, ref hist1, ref hist2);
            }

            return samples.ToArray();
        }

        /// <summary>
        /// Decode a mono PS-ADPCM byte stream (no voice padding logic, raw frames).
        /// Skips only 0xFF frames. Used for channel-separated data.
        /// </summary>
        static short[] DecodeRawChannel(byte[] data, int offset, int length)
        {
            var samples = new List<short>();
            double hist1 = 0.0, hist2 = 0.0;

            for (int i = offset; i + 16 <= offset + length; i += 16)
            {
                if (IsAllFF(data, i))
                    continue;
                DecodeVagFrame(data, i, samples, ref hist1, ref hist2);
            }

            return samples.ToArray();
        }

        #endregion

        #region WAV Writing

        /// <summary>
        /// Write mono 16-bit PCM WAV file.
        /// </summary>
        public static void WriteWav(string path, short[] samples, int sampleRate)
        {
            WriteWavInternal(path, samples, null, sampleRate, 1);
        }

        /// <summary>
        /// Write stereo 16-bit PCM WAV file.
        /// </summary>
        public static void WriteStereoWav(string path, short[] left, short[] right, int sampleRate)
        {
            WriteWavInternal(path, left, right, sampleRate, 2);
        }

        static void WriteWavInternal(string path, short[] left, short[] right,
            int sampleRate, int channels)
        {
            int frameCount = left.Length;
            if (channels == 2 && right != null)
                frameCount = Math.Min(left.Length, right.Length);

            int dataSize = frameCount * channels * 2;
            int bitsPerSample = 16;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;

            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                // RIFF header
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt chunk
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1); // PCM
                bw.Write((short)channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write((short)blockAlign);
                bw.Write((short)bitsPerSample);

                // data chunk
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);

                if (channels == 1)
                {
                    for (int i = 0; i < frameCount; i++)
                        bw.Write(left[i]);
                }
                else
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        bw.Write(left[i]);
                        bw.Write(right[i]);
                    }
                }
            }
        }

        #endregion

        #region VOICE_ID Extraction

        /// <summary>
        /// Extract all voices from VOICE_ID.BIN.
        ///
        /// VOICE_ID.BIN is a continuous PS-ADPCM stream. Voice boundaries are
        /// marked by flag=1 frames. HD slot sizes do NOT align with voice boundaries
        /// (a voice can span multiple slots), so we always use flag=1 scanning.
        ///
        /// HD is only used to report the expected slot count for reference.
        /// </summary>
        public static void ExtractVoice(string binPath, string outPath, string hdPath = null,
            int sampleRate = 22050)
        {
            Directory.CreateDirectory(outPath);

            // HD is informational only
            if (hdPath != null && File.Exists(hdPath))
            {
                uint[] hdSizes = ReadVoiceHD(hdPath);
                Console.WriteLine("HD index: " + Path.GetFileName(hdPath) +
                    " (" + hdSizes.Length + " slots, informational only)");
            }

            // 扫描所有 flag=1 位置——这是唯一可靠的语音分割方式
            Console.WriteLine("Scanning flag=1 positions in " +
                Path.GetFileName(binPath) + "...");
            List<long> flag1Positions = ScanFlag1Positions(binPath);
            int nVoices = flag1Positions.Count;
            Console.WriteLine("  Found " + nVoices + " voices");

            Console.WriteLine("Extracting voices...");
            int emptyCount = 0;

            using (var fs = File.OpenRead(binPath))
            {
                for (int idx = 0; idx < nVoices; idx++)
                {
                    // 从上一个 flag=1+16 到当前 flag=1+16
                    long voiceStart = (idx == 0) ? 0 : flag1Positions[idx - 1] + 16;
                    long voiceEnd = flag1Positions[idx] + 16;
                    int chunkSize = (int)(voiceEnd - voiceStart);

                    if (chunkSize <= 0)
                    {
                        emptyCount++;
                        continue;
                    }

                    byte[] chunk = new byte[chunkSize];
                    fs.Seek(voiceStart, SeekOrigin.Begin);
                    fs.Read(chunk, 0, chunkSize);

                    short[] samples = DecodeVoice(chunk);
                    if (samples.Length == 0)
                    {
                        emptyCount++;
                        continue;
                    }

                    string wavPath = Path.Combine(outPath, string.Format("{0:D5}.wav", idx));
                    WriteWav(wavPath, samples, sampleRate);

                    if ((idx + 1) % 1000 == 0 || idx == nVoices - 1)
                        Console.WriteLine(string.Format("  [{0}/{1}] extracted", idx + 1, nVoices));
                }
            }

            Console.WriteLine("Done!");
            Console.WriteLine("  Total voices: " + nVoices);
            Console.WriteLine("  Empty/silent: " + emptyCount);
            Console.WriteLine("  WAV files: " + (nVoices - emptyCount));
            Console.WriteLine("  Output: " + outPath);
        }

        #endregion

        #region SOUND_ID Extraction

        /// <summary>
        /// Decode a stereo interleaved PS-ADPCM bank.
        /// Data layout: [L_block0][R_block0][L_block1][R_block1]...
        /// Each block = interleave bytes.
        /// </summary>
        static void DecodeInterleavedBank(byte[] data, int interleave,
            out short[] leftSamples, out short[] rightSamples)
        {
            int nBlocks = data.Length / interleave;

            // Separate L/R channels
            int lSize = 0, rSize = 0;
            for (int i = 0; i < nBlocks; i += 2)
            {
                lSize += interleave;
                if (i + 1 < nBlocks) rSize += interleave;
            }

            byte[] lData = new byte[lSize];
            byte[] rData = new byte[rSize];
            int lPos = 0, rPos = 0;

            for (int i = 0; i < nBlocks; i += 2)
            {
                Array.Copy(data, i * interleave, lData, lPos, interleave);
                lPos += interleave;
                if (i + 1 < nBlocks)
                {
                    Array.Copy(data, (i + 1) * interleave, rData, rPos, interleave);
                    rPos += interleave;
                }
            }

            leftSamples = DecodeRawChannel(lData, 0, lPos);
            rightSamples = DecodeRawChannel(rData, 0, rPos);
        }

        /// <summary>
        /// Extract all audio from SOUND_ID.BIN (BGM banks + SE).
        /// Requires SOUND_ID.HD for bank size index.
        /// </summary>
        public static void ExtractSound(string binPath, string hdPath, string outPath,
            int sampleRate = 0)
        {
            Directory.CreateDirectory(outPath);

            if (!File.Exists(hdPath))
            {
                Console.WriteLine("Error: SOUND_ID.HD not found: " + hdPath);
                return;
            }

            // Read header to get interleave size
            byte[] header = new byte[16];
            using (var fs = File.OpenRead(binPath))
                fs.Read(header, 0, 16);
            int interleave = (int)BitConverter.ToUInt32(header, 12);
            int binSampleRate = (int)BitConverter.ToUInt32(header, 4); // 采样率从BIN头读取

            // 确定最终采样率：参数指定 > BIN头读取
            if (sampleRate <= 0)
                sampleRate = binSampleRate;

            // Read HD index (universal parser for all game versions)
            List<uint> banks = ReadSoundHD(hdPath);

            long binSize = new FileInfo(binPath).Length;
            long bankTotal = 0;
            foreach (uint s in banks) bankTotal += s;
            long seRegionOffset = 16 + bankTotal;
            long seRegionSize = binSize - seRegionOffset;

            Console.WriteLine("SOUND_ID Audio Extractor");
            Console.WriteLine("  BIN: " + Path.GetFileName(binPath) +
                " (" + (binSize / 1024 / 1024) + " MB)");
            Console.WriteLine("  Sample rate: " + sampleRate + " Hz");
            Console.WriteLine("  Interleave: " + interleave + " bytes");
            Console.WriteLine("  BGM banks: " + banks.Count);
            Console.WriteLine("  SE region: " + (seRegionSize / 1024) + " KB");

            // Extract BGM banks (stereo)
            Console.WriteLine("\n[1/2] Extracting BGM banks (stereo)...");
            int emptyBanks = 0;

            using (var fs = File.OpenRead(binPath))
            {
                long offset = 16; // skip header

                for (int i = 0; i < banks.Count; i++)
                {
                    int sz = (int)banks[i];
                    byte[] bankData = new byte[sz];
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Read(bankData, 0, sz);
                    offset += sz;

                    short[] left, right;
                    DecodeInterleavedBank(bankData, interleave, out left, out right);

                    int minLen = Math.Min(left.Length, right.Length);
                    if (minLen == 0) { emptyBanks++; continue; }

                    string wavPath = Path.Combine(outPath, string.Format("bgm{0:D3}.wav", i));
                    WriteStereoWav(wavPath, left, right, sampleRate);

                    Console.WriteLine(string.Format("  bgm{0:D3}.wav - {1:F1}s stereo",
                        i, (double)minLen / sampleRate));
                }

                // Extract SE (mono, flag=1 delimited)
                Console.WriteLine("\n[2/2] Extracting SE (mono)...");
                fs.Seek(seRegionOffset, SeekOrigin.Begin);
                byte[] seData = new byte[seRegionSize];
                fs.Read(seData, 0, (int)seRegionSize);

                List<int> seFlag1 = ScanFlag1InRegion(seData);
                int nSE = seFlag1.Count;
                Console.WriteLine("  Found " + nSE + " SE entries");

                int emptySE = 0;
                for (int i = 0; i < nSE; i++)
                {
                    int chunkStart = (i == 0) ? 0 : seFlag1[i - 1] + 16;
                    int chunkEnd = seFlag1[i] + 16;
                    int chunkSize = chunkEnd - chunkStart;

                    byte[] chunk = new byte[chunkSize];
                    Array.Copy(seData, chunkStart, chunk, 0, chunkSize);

                    short[] samples = DecodeVoice(chunk);
                    if (samples.Length == 0) { emptySE++; continue; }

                    string wavPath = Path.Combine(outPath, string.Format("se{0:D3}.wav", i));
                    WriteWav(wavPath, samples, sampleRate);
                }

                Console.WriteLine("  SE: " + (nSE - emptySE) + " extracted, " + emptySE + " empty");
            }

            Console.WriteLine("\nDone!");
            Console.WriteLine("  BGM: " + (banks.Count - emptyBanks) +
                " stereo WAV files");
            Console.WriteLine("  SE: extracted to " + outPath);
        }

        #endregion

        #region Utilities

        static bool IsAllFF(byte[] data, int offset = 0)
        {
            for (int i = 0; i < 16; i++)
            {
                if (data[offset + i] != 0xFF) return false;
            }
            return true;
        }

        #endregion
    }
}
