using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Luna.Engines;

/// <summary>
/// Calls the built-in Windows PDF renderer and OCR engine through raw WinRT vtables, so the app
/// needs neither the 25 MB Windows SDK projection nor PowerShell. Slot numbers come from the
/// interface definitions in C:\Windows\System32\WinMetadata (IInspectable occupies slots 0-5).
/// </summary>
internal static unsafe class WinRt
{
    static readonly Guid IID_IAsyncInfo = new("00000036-0000-0000-C000-000000000046");
    static readonly Guid IID_IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    static readonly Guid IID_IRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
    static readonly Guid IID_IPdfDocumentStatics = new("433A0B5F-C007-4788-90F2-08143D922599");
    static readonly Guid IID_IPdfPageRenderOptions = new("3C98056F-B7CF-4C29-9A04-52D90267F425");
    static readonly Guid IID_IBitmapDecoderStatics = new("438CCB26-BCEF-4E95-BAD6-23A822E58D01");
    static readonly Guid IID_IBitmapEncoderStatics = new("A74356A7-A4E4-4EB9-8E40-564DE7E1CCB2");
    static readonly Guid IID_IBitmapFrameWithSoftwareBitmap = new("FE287C9A-420C-4963-87AD-691436E08383");
    static readonly Guid IID_IOcrEngineStatics = new("5BFFA85A-3384-3540-9940-699120D428A8");

    const int FileAccessRead = 0, FileAccessReadWrite = 1;
    const int PixelFormatBgra8 = 87, AlphaPremultiplied = 0;
    const int MaxRenderSide = 8000;

    [StructLayout(LayoutKind.Sequential)] struct WinSize { public float Width, Height; }

    [DllImport("combase.dll")] static extern int RoInitialize(int type);
    [DllImport("combase.dll")] static extern void RoUninitialize();
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("shcore.dll", CharSet = CharSet.Unicode)]
    static extern int CreateRandomAccessStreamOnFile(string path, int accessMode, in Guid riid, out IntPtr stream);

    // ---------- public API ----------

    /// <summary>Renders every page of a PDF to PNG or JPG files named outBase.ext or outBase-01.ext...</summary>
    public static List<string> RenderPdf(string pdf, string outBase, string fmt, double scale, CancellationToken ct, Action<double> progress) => OnMta(() =>
    {
        IntPtr stream = 0, statics = 0, op = 0, doc = 0, encoders = 0;
        var outputs = new List<string>();
        try
        {
            Check(CreateRandomAccessStreamOnFile(pdf, FileAccessRead, IID_IRandomAccessStream, out stream));
            statics = Factory("Windows.Data.Pdf.PdfDocument", IID_IPdfDocumentStatics);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 8))(statics, stream, &op)); // LoadFromStreamAsync
            try { doc = Result(op, ct); }
            catch (COMException) { throw new ToolException("Windows couldn't open this PDF (is it password-protected?)."); }

            uint count;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(doc, 7))(doc, &count)); // get_PageCount
            Guid? encoder = null;
            if (fmt == "jpg")
            {
                encoders = Factory("Windows.Graphics.Imaging.BitmapEncoder", IID_IBitmapEncoderStatics);
                Guid jpeg;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)Slot(encoders, 7))(encoders, &jpeg)); // get_JpegEncoderId
                encoder = jpeg;
            }
            int digits = Math.Max(2, count.ToString().Length);
            for (uint i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var path = count == 1 ? $"{outBase}.{fmt}" : $"{outBase}-{(i + 1).ToString().PadLeft(digits, '0')}.{fmt}";
                try { RenderPage(doc, i, path, scale, encoder, ct); }
                catch { try { File.Delete(path); } catch { } throw; }
                outputs.Add(path);
                progress((i + 1.0) / count);
            }
            return outputs;
        }
        finally { Release(doc); Release(op); Release(statics); Release(encoders); Release(stream); }
    });

    static void RenderPage(IntPtr doc, uint index, string path, double scale, Guid? encoder, CancellationToken ct)
    {
        IntPtr page = 0, inspectable = 0, opts = 0, output = 0, action = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(doc, 6))(doc, index, &page)); // GetPage
            WinSize size;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, WinSize*, int>)Slot(page, 10))(page, &size)); // get_Size (DIPs)
            double s = Math.Min(scale, MaxRenderSide / Math.Max(1, Math.Max(size.Width, size.Height)));

            var cls = HString("Windows.Data.Pdf.PdfPageRenderOptions");
            try { Check(RoActivateInstance(cls, out inspectable)); } finally { WindowsDeleteString(cls); }
            opts = QueryInterface(inspectable, IID_IPdfPageRenderOptions);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(opts, 9))(opts, (uint)Math.Round(size.Width * s)));   // put_DestinationWidth
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(opts, 11))(opts, (uint)Math.Round(size.Height * s))); // put_DestinationHeight
            if (encoder is Guid g)
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid, int>)Slot(opts, 17))(opts, g)); // put_BitmapEncoderId

            File.WriteAllBytes(path, Array.Empty<byte>()); // the stream API only opens existing files
            Check(CreateRandomAccessStreamOnFile(path, FileAccessReadWrite, IID_IRandomAccessStream, out output));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(page, 7))(page, output, opts, &action)); // RenderWithOptionsToStreamAsync
            Wait(action, ct);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(action, 8))(action)); // IAsyncAction.GetResults
            Close(output);
        }
        finally { Release(action); Release(output); Release(opts); Release(inspectable); Release(page); }
    }

    /// <summary>Recognizes text in an image with the OCR engine for the user's Windows languages.</summary>
    public static string Ocr(string image, CancellationToken ct) => OnMta(() =>
    {
        IntPtr stream = 0, decoders = 0, op1 = 0, decoder = 0, frame = 0, op2 = 0, bitmap = 0;
        IntPtr ocr = 0, engine = 0, op3 = 0, result = 0, lines = 0;
        try
        {
            Check(CreateRandomAccessStreamOnFile(image, FileAccessRead, IID_IRandomAccessStream, out stream));
            decoders = Factory("Windows.Graphics.Imaging.BitmapDecoder", IID_IBitmapDecoderStatics);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(decoders, 14))(decoders, stream, &op1)); // CreateAsync
            decoder = Result(op1, ct);
            frame = QueryInterface(decoder, IID_IBitmapFrameWithSoftwareBitmap);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)Slot(frame, 7))(frame, PixelFormatBgra8, AlphaPremultiplied, &op2)); // GetSoftwareBitmapConvertedAsync
            bitmap = Result(op2, ct);

            ocr = Factory("Windows.Media.Ocr.OcrEngine", IID_IOcrEngineStatics);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(ocr, 10))(ocr, &engine)); // TryCreateFromUserProfileLanguages
            if (engine == 0) throw new ToolException("No OCR language is installed (Settings > Time & language > Language).");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(engine, 6))(engine, bitmap, &op3)); // RecognizeAsync
            result = Result(op3, ct);

            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(result, 6))(result, &lines)); // get_Lines
            uint n;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(lines, 7))(lines, &n));         // IVectorView.get_Size
            var sb = new StringBuilder();
            for (uint i = 0; i < n; i++)
            {
                IntPtr line = 0, text = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(lines, 6))(lines, i, &line)); // GetAt
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(line, 7))(line, &text));           // IOcrLine.get_Text
                    sb.AppendLine(ReadHString(text));
                }
                finally { if (text != 0) WindowsDeleteString(text); Release(line); }
            }
            return sb.ToString().TrimEnd();
        }
        finally
        {
            Release(lines); Release(result); Release(op3); Release(engine); Release(ocr);
            Release(bitmap); Release(op2); Release(frame); Release(decoder); Release(op1); Release(decoders); Release(stream);
        }
    });

    // ---------- plumbing ----------

    static T OnMta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            int hr = RoInitialize(1); // RO_INIT_MULTITHREADED
            try { result = work(); }
            catch (Exception e) { error = e; }
            finally { if (hr >= 0) RoUninitialize(); }
        }) { IsBackground = true, Name = "winrt" };
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Throw(error);
        return result;
    }

    static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    static void Release(IntPtr obj)
    {
        if (obj != 0) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
    }

    static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(obj, 0))(obj, &iid, &result));
        return result;
    }

    static void Close(IntPtr obj)
    {
        IntPtr closable = 0;
        try
        {
            closable = QueryInterface(obj, IID_IClosable);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(closable, 6))(closable);
        }
        catch { }
        finally { Release(closable); }
    }

    static IntPtr HString(string s)
    {
        Check(WindowsCreateString(s, s.Length, out var h));
        return h;
    }

    static string ReadHString(IntPtr h)
    {
        if (h == 0) return "";
        char* buffer = WindowsGetStringRawBuffer(h, out uint length);
        return new string(buffer, 0, (int)length);
    }

    static IntPtr Factory(string runtimeClass, Guid iid)
    {
        var cls = HString(runtimeClass);
        try
        {
            Check(RoGetActivationFactory(cls, iid, out var factory));
            return factory;
        }
        finally { WindowsDeleteString(cls); }
    }

    /// <summary>Polls IAsyncInfo until the operation finishes (no completion callback needed).</summary>
    static void Wait(IntPtr operation, CancellationToken ct)
    {
        IntPtr info = QueryInterface(operation, IID_IAsyncInfo);
        try
        {
            var sw = Stopwatch.StartNew();
            int status;
            while (true)
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status)); // get_Status
                if (status != 0) break; // 0 = Started
                if (ct.IsCancellationRequested || sw.Elapsed > TimeSpan.FromMinutes(5))
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, 9))(info); // Cancel
                    ct.ThrowIfCancellationRequested();
                    throw new ToolException("Windows took too long to finish.");
                }
                Thread.Sleep(5);
            }
            if (status == 3) // Error
            {
                int error;
                ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 8))(info, &error); // get_ErrorCode
                Marshal.ThrowExceptionForHR(error);
            }
            if (status == 2) throw new OperationCanceledException();
        }
        finally { Release(info); }
    }

    /// <summary>Waits for IAsyncOperation&lt;T&gt; and returns GetResults() (slot 8).</summary>
    static IntPtr Result(IntPtr operation, CancellationToken ct)
    {
        Wait(operation, ct);
        IntPtr result;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(operation, 8))(operation, &result));
        return result;
    }
}
