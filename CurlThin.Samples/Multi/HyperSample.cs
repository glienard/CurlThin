using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CurlThin.Enums;
using CurlThin.Helpers;
using CurlThin.HyperPipe;
using CurlThin.Native;
using CurlThin.SafeHandles;

namespace CurlThin.Samples.Multi
{
    internal class HyperSample : ISample
    {
        public void Run()
        {
            int MaxThreads = 8;
            DateTime dtStart = DateTime.Now;
            Console.WriteLine(dtStart);
            if (CurlNative.Init() != CURLcode.OK)
            {
                throw new Exception("Could not init curl");
            }

            var reqProvider = new MyRequestProvider();
            var resConsumer = new MyResponseConsumer();

            using (var pipe = new HyperPipe<MyRequestContext>(MaxThreads, reqProvider, resConsumer))
            {
                pipe.RunLoopWait();
            }
            Console.WriteLine($"Took {DateTime.Now.Subtract(dtStart).TotalSeconds} secs using {MaxThreads} threads={DateTime.Now.Subtract(dtStart).TotalSeconds / MaxThreads} secs per request");
            
        }
    }

    /// <summary>
    ///     What exactly is request context? It can be any type (string, int, custom class, whatever) that will help pass some
    ///     data to method that will process response.
    /// </summary>
    public class MyRequestContext : IDisposable
    {
        public MyRequestContext(string label)
        {
            Label = label;
        }

        public string Label { get; }
        public DataCallbackCopier HeaderData { get; } = new DataCallbackCopier();
        public DataCallbackCopier ContentData { get; } = new DataCallbackCopier();

        public void Dispose()
        {
            HeaderData?.Dispose();
            ContentData?.Dispose();
        }
    }

    /// <summary>
    ///     Request provider generates requests that you want to send to cURL.
    ///     This example shows how to web scrape StackOverflow questions (https://stackoverflow.com/)
    ///     beginning with ID 4400000 until ID 4400050.
    /// </summary>
    public class MyRequestProvider : IRequestProvider<MyRequestContext>
    {
        //private readonly int _maxQuestion = 4400050;
        //private int _currentQuestion = 4400000;
        private int _currentItem = 0;
        private List<string> ips = new List<string>();
        public MyRequestProvider(string ComputerName="VPS1")
        {
            #region Get list of IP addresses

            if (string.IsNullOrEmpty(ComputerName))
                ComputerName = Environment.MachineName;

            var files = Directory.GetFiles(Directory.GetCurrentDirectory(), $"IPv6-{ComputerName}-*.txt");
            if (files.Count() == 0)
            {
                Console.WriteLine($"IPv6-{ComputerName}-*.txt not found!");
                return;
            }
            string strRegex = @"IPv6\-" + ComputerName + @"\-(?<country>\w{2})\.txt";
            Regex reGetCountry = new Regex(strRegex, RegexOptions.IgnoreCase);
            var m = reGetCountry.Match(files[0]);
            if (!m.Success)
            {
                Console.WriteLine($"No country found in {files[0]}");
                return;
            }
            var country = m.Groups["country"].Value;
            ips = File.ReadAllLines(files[0]).Select(x => x.Trim().ToLower()).Distinct().Take(100).ToList();
            

            #endregion
        }

        public MyRequestContext Current { get; private set; }

        public ValueTask<bool> MoveNextAsync(SafeEasyHandle easy)
        {
            // If question ID is higher than maximum, return false.
            if (_currentItem >= ips.Count())
            {
                Current = null;
                return new ValueTask<bool>(false);
            }

            // Create request context. Assign it a label to easily recognize it later.
            var context = new MyRequestContext($"Get from ip  #{_currentItem}: {ips[_currentItem]}");

            // Set request URL.
            CurlNative.Easy.SetOpt(easy, CURLoption.URL, $"http://icanhazip.com/");

            // Follow redirects.
            CurlNative.Easy.SetOpt(easy, CURLoption.FOLLOWLOCATION, 1);

            // Set request timeout.
            CurlNative.Easy.SetOpt(easy, CURLoption.TIMEOUT_MS, 30000);

            // Copy response header (it contains HTTP code and response headers, for example
            // "Content-Type") to MemoryStream in our RequestContext.
            CurlNative.Easy.SetOpt(easy, CURLoption.HEADERFUNCTION, context.HeaderData.DataHandler);

            // Copy response body (it for example contains HTML source) to MemoryStream
            // in our RequestContext.
            CurlNative.Easy.SetOpt(easy, CURLoption.WRITEFUNCTION, context.ContentData.DataHandler);

            // Point the certificate bundle file path to verify HTTPS certificates.
            CurlNative.Easy.SetOpt(easy, CURLoption.CAINFO, CurlResources.CaBundlePath);
            CurlNative.Easy.SetOpt(easy, CURLoption.INTERFACE, "host!"+ips[_currentItem]);
            _currentItem++;
            Current = context;
            return new ValueTask<bool>(true);
        }
    }

    /// <summary>
    ///     This class will process HTTP responses.
    /// </summary>
    public class MyResponseConsumer : IResponseConsumer<MyRequestContext>
    {
        public HandleCompletedAction OnComplete(SafeEasyHandle easy, MyRequestContext context, CURLcode errorCode)
        {
            Console.WriteLine($"Request label: {context.Label}.");
            if (errorCode != CURLcode.OK)
            {
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.ForegroundColor = ConsoleColor.White;

                Console.WriteLine($"cURL error code: {errorCode}");
                var pErrorMsg = CurlNative.Easy.StrError(errorCode);
                var errorMsg = Marshal.PtrToStringAnsi(pErrorMsg);
                Console.WriteLine($"cURL error message: {errorMsg}");

                Console.ResetColor();
                Console.WriteLine("--------");
                Console.WriteLine();

                context.Dispose();
                return HandleCompletedAction.ResetHandleAndNext;
            }

            // Get HTTP response code.
            CurlNative.Easy.GetInfo(easy, CURLINFO.RESPONSE_CODE, out int httpCode);
            if (httpCode != 200)
            {
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.ForegroundColor = ConsoleColor.White;

                Console.WriteLine($"Invalid HTTP response code: {httpCode}");

                Console.ResetColor();
                Console.WriteLine("--------");
                Console.WriteLine();

                context.Dispose();
                return HandleCompletedAction.ResetHandleAndNext;
            }
            Console.WriteLine($"Response code: {httpCode}");

            // Get effective URL.
            IntPtr pDoneUrl;
            CurlNative.Easy.GetInfo(easy, CURLINFO.EFFECTIVE_URL, out pDoneUrl);
            var doneUrl = Marshal.PtrToStringAnsi(pDoneUrl);
            Console.WriteLine($"Effective URL: {doneUrl}");

            // Get response body as string.
            var html = context.ContentData.ReadAsString();

            // Scrape question from HTML source.
            //var match = Regex.Match(html, "<title>(.+?)<\\/");
            Console.WriteLine($"Effective IP: {html}");

            //Console.WriteLine($"Question: {match.Groups[1].Value.Trim()}");
            Console.WriteLine("--------");
            Console.WriteLine();

            context.Dispose();
            return HandleCompletedAction.ResetHandleAndNext;
        }
    }
}