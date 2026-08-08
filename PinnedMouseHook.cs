using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Traymetry
{
    /// <summary>
    /// A low-level mouse hook with a thread of its own.
    ///
    /// The system calls a low-level hook on the thread that installed it, and
    /// only while that thread is asking its queue for a message.  A callback
    /// that does not return inside LowLevelHooksTimeout is dropped without a
    /// word: the handle stays valid, the install still reports success, and the
    /// callback is simply never called again.  Installed from an interface
    /// thread - one that draws a layered window and waits on sensor reads, and
    /// has been measured stalling for whole seconds - that is a hook which
    /// works until the first stall and then silently does not.  It is how a
    /// pinned widget lost its right click in 0.9.0-preview.96: the hook was
    /// called once, five milliseconds after it was installed, and never again.
    ///
    /// So the hook is given a thread with nothing else on it and a pump written
    /// out in the open, where it can be seen, stopped and tested.  Whatever the
    /// filter decides has to be decided without touching a control: this is not
    /// the interface thread and never will be.
    /// </summary>
    internal sealed class PinnedMouseHook
    {
        /// <summary>
        /// Judges one mouse message.  Returning a non-zero value swallows it;
        /// returning zero hands it on to whoever is next in the chain.  It runs
        /// on the hook thread, so it may not ask a control anything - and it
        /// has a few hundred milliseconds, not more, before the system stops
        /// calling it.
        /// </summary>
        internal delegate IntPtr Filter(int code, IntPtr wParam, IntPtr lParam);

        private const int LowLevelMouseHook = 14;
        private const uint Quit = 0x0012;
        // WM_APP, which nothing else in this process posts to this thread.
        private const uint PingMessage = 0x8000;

        private readonly MouseHookProc _callback;
        private readonly object _sync = new object();
        private Filter _filter;
        private Thread _thread;
        private ManualResetEvent _started;
        private ManualResetEvent _pinged;
        private volatile uint _threadId;
        private volatile bool _installed;
        private long _lastCallTicks;
        private long _callCount;
        private bool _firstCallSeen;

        internal PinnedMouseHook()
        {
            // Held for as long as the hook is installed: the delegate is the
            // only managed reference the system has, and a collected one is a
            // callback into freed memory.
            _callback = OnMouseMessage;
            _lastCallTicks = DateTime.UtcNow.Ticks;
        }

        internal bool IsRunning
        {
            get { return _thread != null; }
        }

        internal bool Installed
        {
            get { return _installed; }
        }

        internal uint ThreadId
        {
            get { return _threadId; }
        }

        internal long CallCount
        {
            get { return Interlocked.Read(ref _callCount); }
        }

        /// <summary>
        /// When the system last called this hook.  A hook that has been dropped
        /// leaves no other trace, so this is the only thing there is to ask.
        /// </summary>
        internal DateTime LastCallUtc
        {
            get { return new DateTime(Interlocked.Read(ref _lastCallTicks), DateTimeKind.Utc); }
        }

        internal bool Start(Filter filter)
        {
            if (filter == null)
                throw new ArgumentNullException("filter");
            lock (_sync)
            {
                if (_thread != null)
                    return _installed;
                _filter = filter;
                _firstCallSeen = false;
                Interlocked.Exchange(ref _callCount, 0);
                Interlocked.Exchange(ref _lastCallTicks, DateTime.UtcNow.Ticks);
                _started = new ManualResetEvent(false);
                _pinged = new ManualResetEvent(false);
                Thread pump = new Thread(Run);
                pump.IsBackground = true;
                pump.Name = "traymetry-mouse-hook";
                pump.Start();
                _thread = pump;
            }
            // Waited on so that a liveness check on the very next tick does not
            // find a thread which has not reached SetWindowsHookEx yet and call
            // it dead.  A thread start and a hook install are over in single
            // milliseconds; the wait is only here to have a bound.
            _started.WaitOne(5000);
            return _installed;
        }

        internal void Stop()
        {
            Thread pump;
            lock (_sync)
            {
                pump = _thread;
                _thread = null;
            }
            if (pump == null)
                return;
            uint id = _threadId;
            if (id != 0)
                PostThreadMessage(id, Quit, IntPtr.Zero, IntPtr.Zero);
            // Waited for, so a hook thread is never left running beside its
            // replacement: two of them would both swallow the same press, and
            // the second would put the button back down over the first.
            if (!pump.Join(5000))
                DiagLog.Write("mouse hook thread did not stop in time");
            _threadId = 0;
            _installed = false;
        }

        /// <summary>
        /// Whether the hook thread is still taking messages out of its queue.
        ///
        /// This is the property the hook actually rests on, and the one that
        /// cannot be read from the outside: a thread that has stopped pumping
        /// still exists, still holds a valid hook handle, and still looks
        /// entirely healthy from every other angle.  Asked by the self-test,
        /// where an answer of no is the regression.
        /// </summary>
        internal bool Ping(int milliseconds)
        {
            uint id = _threadId;
            ManualResetEvent answered = _pinged;
            if (id == 0 || answered == null || !_installed)
                return false;
            answered.Reset();
            if (!PostThreadMessage(id, PingMessage, IntPtr.Zero, IntPtr.Zero))
                return false;
            return answered.WaitOne(milliseconds);
        }

        private void Run()
        {
            _threadId = CurrentThreadId();
            // Forces the message queue into being before anyone can post to it.
            // A thread has no queue until it asks for a message, and a
            // PostThreadMessage to a thread without one is thrown away - which
            // would be a hook thread that never stops.
            NativeMessage warm;
            PeekMessage(out warm, IntPtr.Zero, 0, 0, 0);
            IntPtr hook = SetWindowsHookEx(LowLevelMouseHook, _callback,
                GetModuleHandle(null), 0);
            int failure = Marshal.GetLastWin32Error();
            _installed = hook != IntPtr.Zero;
            _started.Set();
            DiagLog.Write("mouse hook installed=" + (_installed ? "1" : "0") +
                " handle=" + hook.ToInt64() +
                " error=" + failure.ToString(CultureInfo.InvariantCulture) +
                " thread=" + _threadId.ToString(CultureInfo.InvariantCulture) +
                " own=1");
            if (hook == IntPtr.Zero)
                return;
            try
            {
                NativeMessage message;
                // The pump the hook is for.  This is not idling: without it the
                // system has nowhere to call.
                while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.Message == PingMessage)
                        _pinged.Set();
                }
            }
            finally
            {
                UnhookWindowsHookEx(hook);
                _installed = false;
                DiagLog.Write("mouse hook removed");
            }
        }

        private IntPtr OnMouseMessage(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                Interlocked.Exchange(ref _lastCallTicks, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref _callCount);
                if (!_firstCallSeen)
                {
                    _firstCallSeen = true;
                    // Proof that the hook is being called at all.  A hook that
                    // is installed and never heard from is indistinguishable,
                    // from outside the program, from a menu that refuses to
                    // open, and the two are fixed in different places.
                    DiagLog.Write("mouse hook called for the first time" +
                        " thread=" + _threadId.ToString(CultureInfo.InvariantCulture) +
                        " message=" + wParam.ToInt64());
                }
                Filter filter = _filter;
                if (filter != null)
                {
                    IntPtr verdict = filter(code, wParam, lParam);
                    if (verdict != IntPtr.Zero)
                        return verdict;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private delegate IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, MouseHookProc callback,
            IntPtr module, uint thread);

        [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code,
            IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        private static extern int GetMessage(out NativeMessage message,
            IntPtr window, uint first, uint last);

        [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out NativeMessage message,
            IntPtr window, uint first, uint last, uint remove);

        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint thread, uint message,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static extern uint CurrentThreadId();
    }

    /// <summary>
    /// Proves, at build time, the things a pinned widget's right click rests
    /// on.  0.9.0-preview.96 shipped with that click dead and nothing in the
    /// build said a word, because every part of it reported success: the hook
    /// installed, the handle was valid, the code compiled.  What was missing
    /// was the one property none of that covers - that the thread holding the
    /// hook is a thread which does nothing but take messages out of its queue.
    /// </summary>
    internal static class MouseHookSelfTest
    {
        private const uint MoveFlag = 0x0001;
        private const uint DesktopReadObjects = 0x0001;

        internal static void Run()
        {
            PinnedMouseHook hook = new PinnedMouseHook();
            long seen = 0;
            PinnedMouseHook.Filter filter = delegate(int code, IntPtr wParam, IntPtr lParam)
            {
                Interlocked.Increment(ref seen);
                return IntPtr.Zero;
            };

            if (!hook.Start(filter))
                throw new InvalidOperationException(
                    "The mouse hook did not install.");
            try
            {
                if (!hook.IsRunning)
                    throw new InvalidOperationException(
                        "The mouse hook reported no thread after starting.");
                // The regression itself, stated as an assertion.  A hook on the
                // thread that asked for it is a hook on a thread with other
                // work to do, and that is the whole fault.
                if (hook.ThreadId == 0 || hook.ThreadId == CurrentThreadId())
                    throw new InvalidOperationException(
                        "The mouse hook was installed on the calling thread " +
                        "instead of one of its own.");
                // And the property that thread exists for.  A thread that has
                // stopped pumping still exists and still holds a valid handle;
                // this is the only question that tells them apart.
                if (!hook.Ping(5000))
                    throw new InvalidOperationException(
                        "The mouse hook thread did not answer its message queue.");

                CheckInputReachesTheHook(hook, ref seen);
            }
            finally
            {
                hook.Stop();
            }
            if (hook.IsRunning || hook.Installed)
                throw new InvalidOperationException(
                    "The mouse hook did not stop.");

            // Started and stopped twice, because the liveness check puts the
            // hook back by doing exactly this, and a second start that quietly
            // fails is a widget that goes dead at the first stall.
            if (!hook.Start(filter))
                throw new InvalidOperationException(
                    "The mouse hook did not install a second time.");
            try
            {
                if (!hook.Ping(5000))
                    throw new InvalidOperationException(
                        "The restarted mouse hook thread did not answer its " +
                        "message queue.");
            }
            finally
            {
                hook.Stop();
            }
        }

        /// <summary>
        /// Sends one pointer movement and waits for the hook to be told about
        /// it.  Skipped where the desktop will not take synthetic input at all,
        /// which is a machine this test cannot speak about rather than a
        /// failure - but a movement that was accepted and never arrived is the
        /// dead hook, and that is a failure.
        /// </summary>
        private static void CheckInputReachesTheHook(PinnedMouseHook hook, ref long seen)
        {
            Interlocked.Exchange(ref seen, 0);
            // Asked before anything is sent.  A build machine with no input
            // desktop of its own cannot answer this question either way, and a
            // test that fails there fails for a reason that has nothing to do
            // with the code it is testing.
            IntPtr desktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desktop == IntPtr.Zero)
                return;
            CloseDesktop(desktop);
            if (!Move(1) || !Move(-1))
                return;
            for (int waited = 0; waited < 5000; waited += 50)
            {
                if (Interlocked.Read(ref seen) > 0)
                    return;
                Thread.Sleep(50);
            }
            throw new InvalidOperationException(
                "The desktop accepted a pointer movement that the mouse hook " +
                "was never told about.");
        }

        private static bool Move(int by)
        {
            Input input = new Input();
            input.Type = 0;
            input.Mouse.Dx = by;
            input.Mouse.Flags = MoveFlag;
            return SendInput(1, ref input, Marshal.SizeOf(typeof(Input))) == 1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint Data;
            public uint Flags;
            public uint Time;
            public IntPtr Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public MouseInput Mouse;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, ref Input inputs, int size);

        [DllImport("user32.dll", EntryPoint = "OpenInputDesktop", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

        [DllImport("user32.dll", EntryPoint = "CloseDesktop")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        private static extern uint CurrentThreadId();
    }
}
