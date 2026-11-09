using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

// Keep this handle open for the launcher's lifetime. Windows closes it even
// when the terminal is killed, terminating every descendant in the job.
public static class LauncherLifetime
{
    private static IntPtr job;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Attach()
    {
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception();
        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: LimitFlags is at byte 16
        // on both architectures. All other limits remain zero.
        int size = IntPtr.Size == 8 ? 144 : 112;
        IntPtr info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, info, size);
            Marshal.WriteInt32(info, 16, 0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, info, (uint)size))
                throw new Win32Exception();
            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(info); }
    }
}
