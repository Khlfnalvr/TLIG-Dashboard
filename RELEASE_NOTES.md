## v{VERSION} — Student Class and NRP Entered Once

### Fixes

- **"Complete Your Profile" dialog no longer appears at every login.** Class and NRP are saved to the account on the Server and returned at login.
- **Offline fallback.** The Client keeps a local copy if the Server is unreachable and syncs it at the next login.
- **Complete input required.** **Save & Continue** is enabled only after Class and NRP are both filled in.

### Compatibility

- **Update the Server before the Client.** Saving Class and NRP to the account requires Server v{VERSION}.
- **Older Server.** Client v{VERSION} still works, but the dialog appears once per laptop and Class and NRP are not visible to the lecturer.

### Installation

- **New installation.** Install `TLIGDashboard-Server-v{VERSION}-Setup.exe` on the lab PC and `TLIGDashboard-Client-v{VERSION}-Setup.exe` on each laptop.
- **Existing installation.** Open the application and select the update notification to install.
