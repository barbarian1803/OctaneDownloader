﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using ProgressBar = OctaneEngine.ShellProgressBar.ProgressBar;
using ProgressBarOptions = OctaneEngine.ShellProgressBar.ProgressBarOptions;

// ReSharper disable All

namespace OctaneEngine
{
    public static class Engine
    {
        static string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        private static string prettySize(long len)
        {
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            string result = String.Format("{0:0.##} {1}", len, sizes[order]);

            return result;
        }

        /// <summary>
        /// The core octane download function.
        /// </summary>
        /// <param name="url">The string url of the file to be downloaded.</param>
        /// <param name="parts">The number of parts (processes) needed to download the file.</param>
        /// <param name="bufferSize">The buffer size to use to download the file</param>
        /// <param name="showProgress">Show the progressbars?</param>
        /// <param name="outFile">The output file name of the download. Use 'null' to get file name from url.</param>
        /// <param name="doneCallback">Callback to handle download completion</param>
        /// <param name="progressCallback">The callback for progress if not wanting to use the built in progress</param>
        /// <param name="numRetries">Number of times to retry a failed download</param>
        public async static Task DownloadFile(string url, int parts, int bufferSize = 8096, bool showProgress = false,
            string outFile = null!, Action<Boolean> doneCallback = null!, Action<Double> progressCallback = null!, int numRetries = 10)
        {
            if (url == null) throw new ArgumentNullException(nameof(url));
            
            //HTTP Client pool so we don't have to keep making them
            var httpPool = new ObjectPool<HttpClient?>(() =>
                new HttpClient(new RetryHandler(new HttpClientHandler(), numRetries))
                    { MaxResponseContentBufferSize = bufferSize });

            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 10000;

            //Get response length and calculate part sizes
            var responseLength = (await WebRequest.Create(url).GetResponseAsync()).ContentLength;
            var partSize = (long)Math.Floor(responseLength / (parts + 0.0));
            var pieces = new List<FileChunk>();
            var uri = new Uri(url);

            Console.WriteLine("TOTAL SIZE: " + prettySize(responseLength));
            Console.WriteLine("PART SIZE: " + prettySize(partSize) + "\n");

            //Get outfile name from argument or URL
            string filename = outFile ?? Path.GetFileName(uri.LocalPath);

            //Create memory mapped file to hold the file
            var mmf = MemoryMappedFile.CreateFromFile(filename, FileMode.OpenOrCreate, null, responseLength,
                MemoryMappedFileAccess.ReadWrite);

            //Loop to add all the events to the queue
            for (long i = 0; i < responseLength; i += partSize)
            {
                pieces.Add(i + partSize < responseLength
                    ? new FileChunk(i, i + partSize)
                    : new FileChunk(i, responseLength));
            }

            //Options for progress base
            var options = new ProgressBarOptions
            {
                ProgressBarOnBottom = false,
                BackgroundCharacter = '\u2593',
                DenseProgressBar = false,
                DisplayTimeInRealTime = false
            };
            var childOptions = new ProgressBarOptions
            {
                ForegroundColor = ConsoleColor.Cyan,
                BackgroundColor = ConsoleColor.Black,
                CollapseWhenFinished = true,
                DenseProgressBar = false,
                //ProgressCharacter = null,
                BackgroundCharacter = '\u2591',
                DisplayTimeInRealTime = false
            };

            int tasksDone = 0;
            
            try
            {
                if (showProgress)
                {
                    using (var pbar = new ProgressBar(parts, "Downloading File...", options))
                    {
                        await Parallel.ForEachAsync(pieces,
                            new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount },
                            async (piece, cancellationToken) =>
                            {
                                System.Diagnostics.Trace.Listeners.Clear();
                                //Get a http client from the pool and request for the content range
                                
                                var client = httpPool.Get();
                                var request = new HttpRequestMessage { RequestUri = new Uri(url) };
                                
                                request.Headers.Range = new RangeHeaderValue(piece.Start, piece.End);

                                //Request headers so we dont cache the file into memory
                                if (client != null)
                                {
                                    var message = await client.SendAsync(request,
                                        HttpCompletionOption.ResponseHeadersRead,
                                        cancellationToken).ConfigureAwait(false);

                                    if (message.IsSuccessStatusCode)
                                    {
                                        //Get the content stream from the message request
                                        using (var streamToRead = await message.Content
                                            .ReadAsStreamAsync(cancellationToken)
                                            .ConfigureAwait(false))
                                        {
                                            //Create a memory mapped stream to the mmf with the piece offset and size equal to the response size
                                            using (var streams = mmf.CreateViewStream(piece.Start,
                                                message.Content.Headers.ContentLength!.Value,
                                                MemoryMappedFileAccess.ReadWrite))
                                            {
                                                using (var chil =
                                                    pbar.Spawn(
                                                        (int)Math.Round(
                                                            (decimal)(message.Content.Headers.ContentLength /
                                                                      bufferSize)), "", childOptions))
                                                {
                                                    //Copy from the content stream to the mmf stream
                                                    var buffer = new byte[bufferSize];
                                                    int offset, bytesRead;
                                                    // Until we've read everything
                                                    do
                                                    {
                                                        offset = 0;
                                                        // Until the buffer is very nearly full or there's nothing left to read
                                                        do
                                                        {
                                                            bytesRead = await streamToRead.ReadAsync(
                                                                buffer.AsMemory(offset, bufferSize - offset),
                                                                cancellationToken).ConfigureAwait(false);
                                                            offset += bytesRead;
                                                        } while (bytesRead != 0 && offset < bufferSize);

                                                        // Empty the buffer
                                                        if (offset != 0)
                                                        {
                                                            //fileStrm.Write(buffer, 0, offset);
                                                            await streams.WriteAsync(buffer.AsMemory(0, offset),
                                                                cancellationToken).ConfigureAwait(false);
                                                            chil.Tick();
                                                        }
                                                    } while (bytesRead != 0);
                                                    
                                                    Array.Clear(buffer);
                                                }

                                                streams.Flush();
                                                streams.Close();
                                            }

                                            streamToRead.Close();
                                        }
                                    }

                                    pbar.Tick();
                                    message.Dispose();
                                }

                                Interlocked.Increment(ref tasksDone);
                                request.Dispose();
                                httpPool.Return(client);
                            });
                    }
                }

                else
                {
                    await Parallel.ForEachAsync(pieces,
                        new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount },
                        async (piece, cancellationToken) =>
                        {
                            //Get a http client from the pool and request for the content range
                            var client = httpPool.Get();
                            var request = new HttpRequestMessage { RequestUri = new Uri(url) };
                            request.Headers.Range = new RangeHeaderValue(piece.Start, piece.End);
                            
                            //Request headers so we dont cache the file into memory
                            if (client != null)
                            {
                                var message = await client.SendAsync(request,
                                    HttpCompletionOption.ResponseHeadersRead,
                                    cancellationToken).ConfigureAwait(false);

                                if (message.IsSuccessStatusCode)
                                {
                                    //Get the content stream from the message request
                                    using (var streamToRead = await message.Content
                                        .ReadAsStreamAsync(cancellationToken)
                                        .ConfigureAwait(false))
                                    {
                                        //Create a memory mapped stream to the mmf with the piece offset and size equal to the response size
                                        using (var streams = mmf.CreateViewStream(piece.Start,
                                            message.Content.Headers.ContentLength!.Value,
                                            MemoryMappedFileAccess.ReadWrite))
                                        {
                                            //Copy from the content stream to the mmf stream
                                            var buffer = new byte[bufferSize];
                                            int offset, bytesRead;
                                            // Until we've read everything
                                            do
                                            {
                                                offset = 0;
                                                // Until the buffer is very nearly full or there's nothing left to read
                                                do
                                                {
                                                    bytesRead = await streamToRead.ReadAsync(
                                                        buffer.AsMemory(offset, bufferSize - offset),
                                                        cancellationToken);
                                                    offset += bytesRead;
                                                } while (bytesRead != 0 && offset < bufferSize);

                                                // Empty the buffer
                                                if (offset != 0)
                                                {
                                                    //fileStrm.Write(buffer, 0, offset);
                                                    await streams.WriteAsync(buffer.AsMemory(0, offset),
                                                        cancellationToken);
                                                }
                                            } while (bytesRead != 0);

                                            streams.Flush();
                                            streams.Close();
                                        }

                                        streamToRead.Close();
                                    }
                                }

                                message.Content.Dispose();
                                message.Dispose();
                            }

                            request.Dispose();
                            Interlocked.Increment(ref tasksDone);
                            httpPool.Return(client);
                            progressCallback((double)((tasksDone + 0.0)/(parts + 0.0)));
                        });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                doneCallback(false);
            }
            finally
            {
                mmf.Dispose();
                httpPool.Empty();
                doneCallback(true);
            }
        }
    }
}