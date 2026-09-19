using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TermWrap
{
    internal static class PipeSecurityFactory
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
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }
    }
}
