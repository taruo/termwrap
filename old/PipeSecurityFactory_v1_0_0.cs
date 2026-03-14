using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TermWrap
{
    internal static class PipeSecurityFactory_v1_0_0
    {
        public static PipeSecurity Create()
        {
            PipeSecurity security = new PipeSecurity();
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
            }

            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }
    }
}
