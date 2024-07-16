using System;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ConstrainedExecution;
using System.Threading;

namespace wpi
{
    using Win32Exception = System.ComponentModel.Win32Exception;
    using PrivilegeNotHeldException = System.Security.AccessControl.PrivilegeNotHeldException;
    using Microsoft.Win32.SafeHandles;
    using System.Security;
    using System.Security.AccessControl;
    using System.IO;
    using System.Security.Principal;

    [Flags]
    internal enum TokenAccessLevels
    {
        AssignPrimary = 0x00000001,
        Duplicate = 0x00000002,
        Impersonate = 0x00000004,
        Query = 0x00000008,
        QuerySource = 0x00000010,
        AdjustPrivileges = 0x00000020,
        AdjustGroups = 0x00000040,
        AdjustDefault = 0x00000080,
        AdjustSessionId = 0x00000100,

        Read = 0x00020000 | Query,

        Write = 0x00020000 | AdjustPrivileges | AdjustGroups | AdjustDefault,

        AllAccess = 0x000F0000 |
            AssignPrimary |
            Duplicate |
            Impersonate |
            Query |
            QuerySource |
            AdjustPrivileges |
            AdjustGroups |
            AdjustDefault |
            AdjustSessionId,

        MaximumAllowed = 0x02000000
    }

    internal enum SecurityImpersonationLevel
    {
        Anonymous = 0,
        Identification = 1,
        Impersonation = 2,
        Delegation = 3,
    }

    internal enum TokenType
    {
        Primary = 1,
        Impersonation = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LUID
    {
        internal uint LowPart;
        internal uint HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LUID_AND_ATTRIBUTES
    {
        internal LUID Luid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TOKEN_PRIVILEGE
    {
        internal uint PrivilegeCount;
        internal LUID_AND_ATTRIBUTES Privilege;
    }

    internal delegate void PrivilegedCallback(object state);

    internal sealed class Privilege
    {
        internal const int ERROR_ACCESS_DENIED = 0x5;
        internal const int ERROR_NO_TOKEN = 0x3f0;
        internal const int ERROR_NOT_ALL_ASSIGNED = 0x514;
        internal const int ERROR_NO_SUCH_PRIVILEGE = 0x521;
        internal const int ERROR_CANT_OPEN_ANONYMOUS = 0x543;
        internal const uint SE_PRIVILEGE_DISABLED = 0x00000000;
        internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Auto, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern bool LookupPrivilegeValue([In] string lpSystemName, [In] string lpName, [In, Out] ref LUID Luid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern bool OpenProcessToken([In] IntPtr ProcessToken, [In] TokenAccessLevels DesiredAccess, [In, Out] ref SafeTokenHandle TokenHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern bool AdjustTokenPrivileges([In] SafeTokenHandle TokenHandle, [In] bool DisableAllPrivileges, [In] ref TOKEN_PRIVILEGE NewState, [In] uint BufferLength, [In, Out] ref TOKEN_PRIVILEGE PreviousState, [In, Out] ref uint ReturnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern bool OpenThreadToken([In] IntPtr ThreadToken, [In] TokenAccessLevels DesiredAccess, [In] bool OpenAsSelf, [In, Out] ref SafeTokenHandle TokenHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern IntPtr GetCurrentThread();

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern bool DuplicateTokenEx([In] SafeTokenHandle ExistingToken, [In] TokenAccessLevels DesiredAccess, [In] IntPtr TokenAttributes, [In] SecurityImpersonationLevel ImpersonationLevel, [In] TokenType TokenType, [In, Out] ref SafeTokenHandle NewToken);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern bool SetThreadToken([In] IntPtr Thread, [In] SafeTokenHandle Token);

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        internal static extern bool RevertToSelf();

        #region Private static members
        private static LocalDataStoreSlot tlsSlot = Thread.AllocateDataSlot();
        private static HybridDictionary privileges = new HybridDictionary();
        private static HybridDictionary luids = new HybridDictionary();
        private static ReaderWriterLock privilegeLock = new ReaderWriterLock();
        #endregion

        #region Private members
        private bool needToRevert = false;
        private bool initialState = false;
        private bool stateWasChanged = false;
        private LUID luid;
        private readonly Thread currentThread = Thread.CurrentThread;
        private TlsContents tlsContents = null;
        #endregion

        #region Privilege names
        public const string Restore = "SeRestorePrivilege";
        #endregion

        #region LUID caching logic

        //
        // This routine is a wrapper around a hashtable containing mappings
        // of privilege names to luids
        //
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        private static LUID LuidFromPrivilege(string privilege)
        {
            LUID luid;
            luid.LowPart = 0;
            luid.HighPart = 0;

            //
            // Look up the privilege LUID inside the cache
            //

            RuntimeHelpers.PrepareConstrainedRegions();

            try
            {
                privilegeLock.AcquireReaderLock(Timeout.Infinite);

                if (luids.Contains(privilege))
                {
                    luid = (LUID)luids[privilege];

                    privilegeLock.ReleaseReaderLock();
                }
                else
                {
                    privilegeLock.ReleaseReaderLock();

                    if (false == LookupPrivilegeValue(null, privilege, ref luid))
                    {
                        int error = Marshal.GetLastWin32Error();

                        if (error == ERROR_ACCESS_DENIED)
                        {
                            throw new UnauthorizedAccessException("Caller does not have the rights to look up privilege local unique identifier");
                        }
                        else if (error == ERROR_NO_SUCH_PRIVILEGE)
                        {
                            throw new ArgumentException(
                                string.Format("{0} is not a valid privilege name", privilege),
                                "privilege");
                        }
                        else
                        {
                            throw new Win32Exception(error);
                        }
                    }

                    privilegeLock.AcquireWriterLock(Timeout.Infinite);
                }
            }
            finally
            {
                if (privilegeLock.IsReaderLockHeld)
                {
                    privilegeLock.ReleaseReaderLock();
                }

                if (privilegeLock.IsWriterLockHeld)
                {
                    if (!luids.Contains(privilege))
                    {
                        luids[privilege] = luid;
                        privileges[luid] = privilege;
                    }

                    privilegeLock.ReleaseWriterLock();
                }
            }

            return luid;
        }
        #endregion

        internal sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeTokenHandle() : base(true) { }

            // 0 is an Invalid Handle
            internal SafeTokenHandle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            internal static SafeTokenHandle InvalidHandle
            {
                get { return new SafeTokenHandle(IntPtr.Zero); }
            }

            [DllImport("kernel32.dll", SetLastError = true), SuppressUnmanagedCodeSecurity, ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
            private static extern bool CloseHandle(IntPtr handle);

            override protected bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        #region Nested classes
        private sealed class TlsContents : IDisposable
        {
            private bool disposed = false;
            private int referenceCount = 1;
            private SafeTokenHandle threadHandle = new SafeTokenHandle(IntPtr.Zero);
            private bool isImpersonating = false;

            private static SafeTokenHandle processHandle = new SafeTokenHandle(IntPtr.Zero);
            private static readonly object syncRoot = new object();

            #region Constructor and finalizer
            public TlsContents()
            {
                int error = 0;
                int cachingError = 0;
                bool success = true;

                if (processHandle.IsInvalid)
                {
                    lock (syncRoot)
                    {
                        if (processHandle.IsInvalid)
                        {
                            if (false == OpenProcessToken(
                                            GetCurrentProcess(),
                                            TokenAccessLevels.Duplicate,
                                            ref processHandle))
                            {
                                cachingError = Marshal.GetLastWin32Error();
                                success = false;
                            }
                        }
                    }
                }

                RuntimeHelpers.PrepareConstrainedRegions();

                try
                {
                    // Open the thread token; if there is no thread token,
                    // copy the process token onto the thread

                    if (false == OpenThreadToken(
                        GetCurrentThread(),
                        TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges,
                        true,
                        ref this.threadHandle))
                    {
                        if (success == true)
                        {
                            error = Marshal.GetLastWin32Error();

                            if (error != ERROR_NO_TOKEN)
                            {
                                success = false;
                            }

                            if (success == true)
                            {
                                error = 0;

                                if (false == DuplicateTokenEx(
                                    processHandle,
                                    TokenAccessLevels.Impersonate | TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges,
                                    IntPtr.Zero,
                                    SecurityImpersonationLevel.Impersonation,
                                    TokenType.Impersonation,
                                    ref this.threadHandle))
                                {
                                    error = Marshal.GetLastWin32Error();
                                    success = false;
                                }
                            }

                            if (success == true)
                            {
                                if (false == SetThreadToken(
                                    IntPtr.Zero,
                                    this.threadHandle))
                                {
                                    error = Marshal.GetLastWin32Error();
                                    success = false;
                                }
                            }

                            if (success == true)
                            {
                                // This thread is now impersonating; it needs to be reverted to its original state

                                this.isImpersonating = true;
                            }
                        }
                        else
                        {
                            error = cachingError;
                        }
                    }
                    else
                    {
                        success = true;
                    }
                }
                finally
                {
                    if (!success)
                    {
                        Dispose();
                    }
                }

                if (error == ERROR_ACCESS_DENIED ||
                    error == ERROR_CANT_OPEN_ANONYMOUS)
                {
                    throw new UnauthorizedAccessException("The caller does not have the rights to perform the operation");
                }
                else if (error != 0)
                {
                    throw new Win32Exception(error);
                }
            }

            ~TlsContents()
            {
                if (!this.disposed)
                {
                    Dispose(false);
                }
            }
            #endregion

            #region IDisposable implementation
            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing)
            {
                if (this.disposed) return;

                if (this.threadHandle != null)
                {
                    this.threadHandle.Dispose();
                    this.threadHandle = null;
                }

                if (this.isImpersonating)
                {
                    RevertToSelf();
                }

                this.disposed = true;
            }
            #endregion

            #region Reference-counting
            public void IncrementReferenceCount()
            {
                this.referenceCount++;
            }

            public int DecrementReferenceCount()
            {
                int result = --this.referenceCount;

                if (result == 0)
                {
                    Dispose();
                }

                return result;
            }

            public int ReferenceCountValue
            {
                get { return this.referenceCount; }
            }
            #endregion

            #region Properties
            public SafeTokenHandle ThreadHandle
            {
                get { return this.threadHandle; }
            }

            public bool IsImpersonating
            {
                get { return this.isImpersonating; }
            }
            #endregion
        }
        #endregion

        #region Constructor
        public Privilege(string privilegeName)
        {
            if (privilegeName == null)
            {
                throw new ArgumentNullException("privilegeName");
            }

            this.luid = LuidFromPrivilege(privilegeName);
        }
        #endregion

        #region Public methods and properties
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        public void Enable()
        {
            this.ToggleState(true);
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        public void Disable()
        {
            this.ToggleState(false);
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        public void Revert()
        {
            int error = 0;

            // All privilege operations must take place on the same thread

            if (!this.currentThread.Equals(Thread.CurrentThread))
            {
                throw new InvalidOperationException("Operation must take place on the thread that created the object");
            }

            if (!this.NeedToRevert)
            {
                return;
            }

            // This code must be eagerly prepared and non-interruptible.

            RuntimeHelpers.PrepareConstrainedRegions();

            try
            {
                // The payload is entirely in the finally block
                // This is how we ensure that the code will not be
                // interrupted by catastrophic exceptions
            }
            finally
            {
                bool success = true;

                try
                {
                    // Only call AdjustTokenPrivileges if we're not going to be reverting to self,
                    // on this Revert, since doing the latter obliterates the thread token anyway

                    if (this.stateWasChanged &&
                        (this.tlsContents.ReferenceCountValue > 1 ||
                        !this.tlsContents.IsImpersonating))
                    {
                        TOKEN_PRIVILEGE newState = new TOKEN_PRIVILEGE();
                        newState.PrivilegeCount = 1;
                        newState.Privilege.Luid = this.luid;
                        newState.Privilege.Attributes = (this.initialState ? SE_PRIVILEGE_ENABLED : SE_PRIVILEGE_DISABLED);

                        TOKEN_PRIVILEGE previousState = new TOKEN_PRIVILEGE();
                        uint previousSize = 0;

                        if (false == AdjustTokenPrivileges(
                                        this.tlsContents.ThreadHandle,
                                        false,
                                        ref newState,
                                        (uint)Marshal.SizeOf(previousState),
                                        ref previousState,
                                        ref previousSize))
                        {
                            error = Marshal.GetLastWin32Error();
                            success = false;
                        }
                    }
                }
                finally
                {
                    if (success)
                    {
                        this.Reset();
                    }
                }
            }

            if (error == ERROR_ACCESS_DENIED)
            {
                throw new UnauthorizedAccessException("Caller does not have the permission to change the privilege");
            }
            else if (error != 0)
            {
                throw new Win32Exception(error);
            }
        }

        public bool NeedToRevert
        {
            get { return this.needToRevert; }
        }

        public static void RunWithPrivilege(string privilege, bool enabled, PrivilegedCallback callback, object state)
        {
            if (callback == null)
            {
                throw new ArgumentNullException("callback");
            }

            Privilege p = new Privilege(privilege);

            RuntimeHelpers.PrepareConstrainedRegions();

            try
            {
                if (enabled)
                {
                    p.Enable();
                }
                else
                {
                    p.Disable();
                }

                callback(state);
            }
            catch
            {
                p.Revert();
                throw;
            }
            finally
            {
                p.Revert();
            }
        }
        #endregion

        #region Private implementation
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        private void ToggleState(bool enable)
        {
            int error = 0;

            // All privilege operations must take place on the same thread

            if (!this.currentThread.Equals(Thread.CurrentThread))
            {
                throw new InvalidOperationException("Operation must take place on the thread that created the object");
            }

            // This privilege was already altered and needs to be reverted before it can be altered again

            if (this.NeedToRevert)
            {
                throw new InvalidOperationException("Must revert the privilege prior to attempting this operation");
            }

            // Need to make this block of code non-interruptible so that it would preserve
            // consistency of thread oken state even in the face of catastrophic exceptions

            RuntimeHelpers.PrepareConstrainedRegions();

            try
            {
                // The payload is entirely in the finally block
                // This is how we ensure that the code will not be
                // interrupted by catastrophic exceptions
            }
            finally
            {
                try
                {
                    // Retrieve TLS state

                    this.tlsContents = Thread.GetData(tlsSlot) as TlsContents;

                    if (this.tlsContents == null)
                    {
                        this.tlsContents = new TlsContents();
                        Thread.SetData(tlsSlot, this.tlsContents);
                    }
                    else
                    {
                        this.tlsContents.IncrementReferenceCount();
                    }

                    TOKEN_PRIVILEGE newState = new TOKEN_PRIVILEGE();
                    newState.PrivilegeCount = 1;
                    newState.Privilege.Luid = this.luid;
                    newState.Privilege.Attributes = enable ? SE_PRIVILEGE_ENABLED : SE_PRIVILEGE_DISABLED;

                    TOKEN_PRIVILEGE previousState = new TOKEN_PRIVILEGE();
                    uint previousSize = 0;

                    // Place the new privilege on the thread token and remember the previous state.

                    if (false == AdjustTokenPrivileges(
                                    this.tlsContents.ThreadHandle,
                                    false,
                                    ref newState,
                                    (uint)Marshal.SizeOf(previousState),
                                    ref previousState,
                                    ref previousSize))
                    {
                        error = Marshal.GetLastWin32Error();
                    }
                    else if (ERROR_NOT_ALL_ASSIGNED == Marshal.GetLastWin32Error())
                    {
                        error = ERROR_NOT_ALL_ASSIGNED;
                    }
                    else
                    {
                        // This is the initial state that revert will have to go back to

                        this.initialState = ((previousState.Privilege.Attributes & SE_PRIVILEGE_ENABLED) != 0);

                        // Remember whether state has changed at all

                        this.stateWasChanged = (this.initialState != enable);

                        // If we had to impersonate, or if the privilege state changed we'll need to revert

                        this.needToRevert = this.tlsContents.IsImpersonating || this.stateWasChanged;
                    }
                }
                finally
                {
                    if (!this.needToRevert)
                    {
                        this.Reset();
                    }
                }
            }

            if (error == ERROR_NOT_ALL_ASSIGNED)
            {
                throw new PrivilegeNotHeldException(privileges[this.luid] as string);
            }
            if (error == ERROR_ACCESS_DENIED ||
                error == ERROR_CANT_OPEN_ANONYMOUS)
            {
                throw new UnauthorizedAccessException("The caller does not have the right to change the privilege");
            }
            else if (error != 0)
            {
                throw new Win32Exception(error);
            }
        }

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        private void Reset()
        {
            RuntimeHelpers.PrepareConstrainedRegions();

            try
            {
                // Payload is in the finally block
                // as a way to guarantee execution
            }
            finally
            {
                this.stateWasChanged = false;
                this.initialState = false;
                this.needToRevert = false;

                if (this.tlsContents != null)
                {
                    if (0 == this.tlsContents.DecrementReferenceCount())
                    {
                        this.tlsContents = null;
                        Thread.SetData(tlsSlot, null);
                    }
                }
            }
        }
        #endregion

        static public FileSecurity prepareFileModification(string filePath)
        {
            // Backup original owner and ACL
            FileSecurity OriginalACL = File.GetAccessControl(filePath);

            // And take the original security to create new security rules.
            FileSecurity NewACL = File.GetAccessControl(filePath);

            // Take ownership
            NewACL.SetOwner(WindowsIdentity.GetCurrent().User);
            File.SetAccessControl(filePath, NewACL);

            // And create a new access rule
            NewACL.SetAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(filePath, NewACL);

            return OriginalACL;
        }

        static public void finishFileModification(string filePath, FileSecurity OriginalACL, Privilege RestorePrivilege)
        {
            // Restore original owner and access rules.
            // The OriginalACL cannot be reused directly.
            FileSecurity NewACL = File.GetAccessControl(filePath);
            NewACL.SetSecurityDescriptorBinaryForm(OriginalACL.GetSecurityDescriptorBinaryForm());
            File.SetAccessControl(filePath, NewACL);

            // Revert to self
            RestorePrivilege.Revert();
            RestorePrivilege.Disable();
        }
    }
}
