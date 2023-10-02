﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DotFastLZ.Compression;

namespace DotFastLZ.Package
{
    public static class SixPack
    {
        public const int SIXPACK_VERSION_MAJOR = 0;
        public const int SIXPACK_VERSION_MINOR = 1;
        public const int SIXPACK_VERSION_REVISION = 0;
        public const string SIXPACK_VERSION_STRING = "snapshot 20070615";

        /* magic identifier for 6pack file */
        private static readonly byte[] sixpack_magic = { 137, (byte)'6', (byte)'P', (byte)'K', 13, 10, 26, 10 };
        public const int BLOCK_SIZE = (2 * 64 * 1024);

        /* for Adler-32 checksum algorithm, see RFC 1950 Section 8.2 */
        public const int ADLER32_BASE = 65521;

        private static ulong update_adler32(ulong checksum, byte[] buf, long len)
        {
            int ptr = 0;
            ulong s1 = checksum & 0xffff;
            ulong s2 = (checksum >> 16) & 0xffff;

            while (len > 0)
            {
                var k = len < 5552 ? len : 5552;
                len -= k;

                while (k >= 8)
                {
                    s1 += buf[ptr++];
                    s2 += s1;
                    s1 += buf[ptr++];
                    s2 += s1;
                    s1 += buf[ptr++];
                    s2 += s1;
                    s1 += buf[ptr++];
                    s2 += s1;
                    s1 += buf[ptr++];
                    s2 += s1;
                    s1 += buf[ptr++];
                    s2 += s1;
                    s1 += buf[ptr++];
                    s2 += s1;
                    s1 += buf[ptr++];
                    s2 += s1;
                    k -= 8;
                }

                while (k-- > 0)
                {
                    s1 += buf[ptr++];
                    s2 += s1;
                }

                s1 = s1 % ADLER32_BASE;
                s2 = s2 % ADLER32_BASE;
            }

            return (s2 << 16) + s1;
        }


        /* return non-zero if magic sequence is detected */
        /* warning: reset the read pointer to the beginning of the file */
        public static bool detect_magic(FileStream f)
        {
            byte[] buffer = new byte[8];

            f.Seek(0, SeekOrigin.Begin);
            var bytesRead = f.Read(buffer, 0, 8);
            f.Seek(0, SeekOrigin.Begin);

            if (bytesRead < 8)
            {
                return false;
            }

            for (int c = 0; c < 8; c++)
            {
                if (buffer[c] != sixpack_magic[c])
                {
                    return false;
                }
            }

            return true;
        }


        public static void write_magic(FileStream f)
        {
            f.Write(sixpack_magic);
        }


        public static void write_chunk_header(FileStream f, int id, int options, long size, ulong checksum, long extra)
        {
            byte[] buffer = new byte[16];

            buffer[0] = (byte)(id & 255);
            buffer[1] = (byte)(id >> 8);
            buffer[2] = (byte)(options & 255);
            buffer[3] = (byte)(options >> 8);
            buffer[4] = (byte)(size & 255);
            buffer[5] = (byte)((size >> 8) & 255);
            buffer[6] = (byte)((size >> 16) & 255);
            buffer[7] = (byte)((size >> 24) & 255);
            buffer[8] = (byte)(checksum & 255);
            buffer[9] = (byte)((checksum >> 8) & 255);
            buffer[10] = (byte)((checksum >> 16) & 255);
            buffer[11] = (byte)((checksum >> 24) & 255);
            buffer[12] = (byte)(extra & 255);
            buffer[13] = (byte)((extra >> 8) & 255);
            buffer[14] = (byte)((extra >> 16) & 255);
            buffer[15] = (byte)((extra >> 24) & 255);

            f.Write(buffer);
        }

        public static string GetFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            /* truncate directory prefix, e.g. "foo/bar/FILE.txt" becomes "FILE.txt" */
            return path
                .Split(new char[] { '/', '\\', Path.DirectorySeparatorChar, }, StringSplitOptions.RemoveEmptyEntries)
                .Last();
        }

        public static int pack_file_compressed(string input_file, int method, int level, FileStream f)
        {
            ulong checksum;
            byte[] result = new byte[BLOCK_SIZE * 2]; /* FIXME twice is too large */

            /* sanity check */
            FileStream temp = OpenFile(input_file, FileMode.Open);
            if (null == temp)
            {
                Console.WriteLine($"Error: could not open {input_file}");
                return -1;
            }

            using var ifs = temp;

            /* find size of the file */
            ifs.Seek(0, SeekOrigin.End);
            long fsize = ifs.Position;
            ifs.Seek(0, SeekOrigin.Begin);

            /* already a 6pack archive? */
            if (detect_magic(ifs))
            {
                Console.WriteLine($"Error: file {input_file} is already a 6pack archive!");
                return -1;
            }

            /* truncate directory prefix, e.g. "foo/bar/FILE.txt" becomes "FILE.txt" */
            string fileName = GetFileName(input_file);
            byte[] utf8_shown_name = Encoding.UTF8.GetBytes(fileName);
            byte[] shown_name = new byte[utf8_shown_name.Length + 1]; // for cstyle
            Array.Fill(shown_name, (byte)0);
            Array.Copy(utf8_shown_name, shown_name, utf8_shown_name.Length);

            /* chunk for File Entry */
            byte[] buffer = new byte[BLOCK_SIZE];
            buffer[0] = (byte)(fsize & 255);
            buffer[1] = (byte)((fsize >> 8) & 255);
            buffer[2] = (byte)((fsize >> 16) & 255);
            buffer[3] = (byte)((fsize >> 24) & 255);
            buffer[4] = (byte)((fsize >> 32) & 255);
            buffer[5] = (byte)((fsize >> 40) & 255);
            buffer[6] = (byte)((fsize >> 48) & 255);
            buffer[7] = (byte)((fsize >> 56) & 255);
            buffer[8] = (byte)(shown_name.Length & 255); // filename length for lowest bit
            buffer[9] = (byte)(shown_name.Length >> 8); // filename length for highest bit

            checksum = 1L;
            checksum = update_adler32(checksum, buffer, 10);
            checksum = update_adler32(checksum, shown_name, shown_name.Length);
            write_chunk_header(f, 1, 0, 10 + shown_name.Length, checksum, 0);
            f.Write(buffer, 0, 10);
            f.Write(shown_name, 0, shown_name.Length);
            long total_compressed = 16 + 10 + shown_name.Length;

            /* for progress status */
            string progress;
            if (16 < fileName.Length)
            {
                progress = fileName.Substring(0, 13);
                progress += ".. ";
            }
            else
            {
                progress = fileName.PadRight(16, ' ');
            }


            Console.Write($"{progress} [");
            for (int c = 0; c < 50; c++)
            {
                Console.Write(".");
            }

            Console.Write("]\r");
            Console.Write($"{progress} [");

            /* read file and place ifs archive */
            long total_read = 0;
            long percent = 0;
            var beginTick = DateTime.UtcNow.Ticks;
            for (;;)
            {
                int compress_method = method;
                int last_percent = (int)percent;
                int bytes_read = ifs.Read(buffer, 0, BLOCK_SIZE);
                if (bytes_read == 0)
                    break;

                total_read += bytes_read;

                /* for progress */
                if (fsize < (1 << 24))
                {
                    percent = total_read * 100 / fsize;
                }
                else
                {
                    percent = total_read / 256 * 100 / (fsize >> 8);
                }

                percent /= 2;
                while (last_percent < (int)percent)
                {
                    Console.Write("#");
                    last_percent++;
                }

                /* too small, don't bother to compress */
                if (bytes_read < 32) compress_method = 0;

                /* write to output */
                switch (compress_method)
                {
                    /* FastLZ */
                    case 1:
                    {
                        long chunkSize = FastLZ.CompressLevel(level, buffer, bytes_read, result);
                        checksum = update_adler32(1L, result, chunkSize);
                        write_chunk_header(f, 17, 1, chunkSize, checksum, bytes_read);
                        f.Write(result, 0, (int)chunkSize);
                        total_compressed += 16;
                        total_compressed += chunkSize;
                    }
                        break;

                    /* uncompressed, also fallback method */
                    case 0:
                    default:
                    {
                        checksum = 1L;
                        checksum = update_adler32(checksum, buffer, bytes_read);
                        write_chunk_header(f, 17, 0, bytes_read, checksum, bytes_read);
                        f.Write(buffer, 0, bytes_read);
                        total_compressed += 16;
                        total_compressed += bytes_read;
                    }
                        break;
                }
            }

            if (total_read != fsize)
            {
                Console.WriteLine("");
                Console.WriteLine($"Error: reading {input_file} failed!");
                return -1;
            }
            else
            {
                Console.Write("] ");
                if (total_compressed < fsize)
                {
                    if (fsize < (1 << 20))
                    {
                        percent = total_compressed * 1000 / fsize;
                    }
                    else
                    {
                        percent = total_compressed / 256 * 1000 / (fsize >> 8);
                    }

                    percent = 1000 - percent;

                    var elapsedTicks = (DateTime.UtcNow.Ticks - beginTick);
                    var elapsedMs = elapsedTicks / TimeSpan.TicksPerMillisecond;
                    var elapsedMicro = elapsedTicks / (TimeSpan.TicksPerMillisecond * 1000);
                    Console.Write($"{(int)percent / 10:D2}.{(int)percent % 10:D1}% saved - {elapsedMs} ms, {elapsedMicro} micro");
                }

                Console.WriteLine("");
            }

            return 0;
        }

        public static FileStream OpenFile(string filePath, FileMode mode, FileAccess access = FileAccess.Read, FileShare share = FileShare.Read)
        {
            try
            {
                return new FileStream(filePath, mode, access, share);
            }
            catch (Exception)
            {
                return null;
            }
        }


        public static int pack_file(int compress_level, string input_file, string output_file)
        {
            int result;

            var fs = OpenFile(output_file, FileMode.Open);
            if (null != fs)
            {
                fs.Dispose();
                Console.WriteLine($"Error: file {output_file} already exists. Aborted.");
                return -1;
            }

            fs = OpenFile(output_file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            if (null == fs)
            {
                Console.WriteLine($"Error: could not create {output_file}. Aborted.");
                return -1;
            }

            using var ofs = fs;

            write_magic(ofs);
            result = pack_file_compressed(input_file, 1, compress_level, ofs);
            return result;
        }

        public static int benchmark_speed(int compress_level, string input_file)
        {
            /* sanity check */
            var fs = OpenFile(input_file, FileMode.Open);
            if (null == fs)
            {
                Console.WriteLine($"Error: could not open {input_file}");
                return -1;
            }

            using var ifs = fs;

            /* find size of the file */
            ifs.Seek(0, SeekOrigin.End);
            var fsize = ifs.Position;
            ifs.Seek(0, SeekOrigin.Begin);

            /* already a 6pack archive? */
            if (detect_magic(ifs))
            {
                Console.WriteLine("Error: no benchmark for 6pack archive!");
                return -1;
            }

            /* truncate directory prefix, e.g. "foo/bar/FILE.txt" becomes "FILE.txt" */
            var shown_name = GetFileName(input_file);

            long maxout = (long)(1.05d * fsize);
            maxout = (maxout < 66) ? 66 : maxout;
            byte[] buffer = new byte[fsize];
            byte[] result = new byte[maxout];

            /* for benchmark */
            // if (null == buffer || null == result)
            // {
            //     Console.WriteLine("Error: not enough memory!");
            //     return -1;
            // }

            Console.WriteLine("Reading source file....");
            int bytes_read = ifs.Read(buffer, 0, 1);
            if (bytes_read != fsize)
            {
                Console.WriteLine($"Error reading file {shown_name}!");
                Console.WriteLine($"Read {bytes_read} bytes, expecting {fsize} bytes");
                return -1;
            }

            /* shamelessly copied from QuickLZ 1.20 test program */
            {
                long mbs, fastest;

                Console.WriteLine("Setting HIGH_PRIORITY_CLASS...");
                {
                    Process currentProcess = Process.GetCurrentProcess();
                    currentProcess.PriorityClass = ProcessPriorityClass.High;
                }

                Console.WriteLine($"Benchmarking FastLZ Level {compress_level}, please wait...");

                long u = 0;
                int i = bytes_read;
                fastest = 0;
                for (int j = 0; j < 3; j++)
                {
                    int y = 0;
                    mbs = DateTime.UtcNow.Ticks;
                    while (DateTime.UtcNow.Ticks == mbs)
                    {
                    }

                    mbs = DateTime.UtcNow.Ticks;
                    while (DateTime.UtcNow.Ticks - mbs < 3000) /* 1% accuracy with 18.2 timer */
                    {
                        u = FastLZ.CompressLevel(compress_level, buffer, bytes_read, result);
                        y++;
                    }


                    mbs = (long)(((double)i * (double)y) / ((double)(DateTime.UtcNow.Ticks - mbs) / 1000.0d) / 1000000.0d);
                    /*printf(" %.1f Mbyte/s  ", mbs);*/
                    if (fastest < mbs) fastest = mbs;
                }

                Console.WriteLine($"Compressed {i} bytes into {u} bytes ({(u * 100.0 / i):F1}%) at {fastest:F1} Mbyte/s.");

                fastest = 0;
                long compressed_size = u;
                for (int j = 0; j < 3; j++)
                {
                    int y = 0;
                    mbs = DateTime.UtcNow.Ticks;
                    while (DateTime.UtcNow.Ticks == mbs)
                    {
                    }

                    mbs = DateTime.UtcNow.Ticks;
                    while (DateTime.UtcNow.Ticks - mbs < 3000) /* 1% accuracy with 18.2 timer */
                    {
                        u = FastLZ.Decompress(result, compressed_size, buffer, bytes_read);
                        y++;
                    }

                    mbs = (long)(((double)i * (double)y) / ((double)(DateTime.UtcNow.Ticks - mbs) / 1000.0d) / 1000000.0d);
                    /*printf(" %.1f Mbyte/s  ", mbs);*/
                    if (fastest < mbs) fastest = mbs;
                }

                Console.WriteLine($"\nDecompressed at {fastest:F1} Mbyte/s.\n\n(1 MB = 1000000 byte)");
            }

            return 0;
        }
    }
}