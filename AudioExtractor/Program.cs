using System;
using System.IO;

namespace AudioExtractor
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("AudioExtractor - PS-ADPCM audio extractor for Zero no Tsukaima PS2");
                Console.WriteLine();
                Console.WriteLine("Usage: Drag VOICE_ID.BIN or SOUND_ID.BIN onto AudioExtractor.exe");
                Console.WriteLine("  The corresponding *.HD file should be in the same directory.");
                Console.WriteLine();
                Console.WriteLine("  AudioExtractor.exe <path_to_BIN> [sample_rate]");
                Console.WriteLine();
                Console.WriteLine("  VOICE_ID.BIN -> mono WAV (default 22050Hz)");
                Console.WriteLine("  SOUND_ID.BIN -> stereo BGM + mono SE (sample rate from BIN header)");
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            // Parse optional sample rate from args
            int customSampleRate = -1;
            for (int i = 0; i < args.Length; i++)
            {
                int sr;
                if (int.TryParse(args[i], out sr) && sr > 0 && sr <= 96000)
                {
                    customSampleRate = sr;
                    break;
                }
            }

            foreach (string arg in args)
            {
                try
                {
                    if (!arg.EndsWith(".BIN", StringComparison.OrdinalIgnoreCase))
                    {
                        int sr;
                        if (int.TryParse(arg, out sr))
                            continue; // skip sample rate arg
                        Console.WriteLine("Not a BIN file: " + arg);
                        continue;
                    }

                    string binPath = Path.GetFullPath(arg);
                    if (!File.Exists(binPath))
                    {
                        Console.WriteLine("File not found: " + binPath);
                        continue;
                    }

                    string fileName = Path.GetFileNameWithoutExtension(binPath).ToUpperInvariant();
                    string binDir = Path.GetDirectoryName(binPath);
                    string outPath = Path.Combine(binDir,
                        Path.GetFileNameWithoutExtension(binPath));
                    string hdPath = Path.ChangeExtension(binPath, ".HD");

                    Console.WriteLine("Input: " + binPath);

                    if (fileName.StartsWith("VOICE"))
                    {
                        // VOICE_ID mode: mono 22050Hz
                        int sampleRate = (customSampleRate > 0) ? customSampleRate : 22050;
                        Console.WriteLine("Mode: VOICE (mono " + sampleRate + "Hz)");
                        Console.WriteLine("HD: " + (File.Exists(hdPath) ? hdPath : "(not found, using flag=1 scan)"));

                        AudioExtractor.ExtractVoice(binPath, outPath,
                            File.Exists(hdPath) ? hdPath : null, sampleRate);
                    }
                    else if (fileName.StartsWith("SOUND"))
                    {
                        // SOUND_ID mode: sample rate from BIN header (or custom override)
                        int sampleRate = (customSampleRate > 0) ? customSampleRate : 0;
                        Console.WriteLine("Mode: SOUND (stereo, " +
                            (customSampleRate > 0 ? customSampleRate + "Hz override" : "sample rate from BIN header") + ")");

                        if (!File.Exists(hdPath))
                        {
                            Console.WriteLine("Error: SOUND_ID.HD is required but not found: " + hdPath);
                            continue;
                        }

                        AudioExtractor.ExtractSound(binPath, hdPath, outPath, sampleRate);
                    }
                    else
                    {
                        Console.WriteLine("Unknown BIN type: " + fileName);
                        Console.WriteLine("  Expected VOICE_ID.BIN or SOUND_ID.BIN");
                        continue;
                    }

                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed: " + arg);
                    Console.WriteLine("  Error: " + ex.Message);
                    continue;
                }
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
    }
}
