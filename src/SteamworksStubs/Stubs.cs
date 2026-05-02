using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Steamworks
{
    // --- Enums ---
    public enum EResult { k_EResultOK = 1, k_EResultFail = 2 }
    public enum ESteamAPIInitResult { OK = 0, Failed = 1, SteamPieceOfTrash = 2 }
    public enum ESteamNetworkingSocketsDebugOutputType { k_ESteamNetworkingSocketsDebugOutputType_None = 0 }
    public enum ESteamNetworkingConnectionState { k_ESteamNetworkingConnectionState_None = 0 }
    public enum ESteamNetConnectionEnd { k_ESteamNetConnectionEnd_App_Generic = 0 }
    public enum EInputType { k_EDeviceType_Unknown = 0 }
    public enum EInputActionOrigin { k_EInputActionOrigin_None = 0 }

    // --- Types & Structs ---
    [StructLayout(LayoutKind.Sequential)]
    public struct AppId_t { public uint m_AppId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CSteamID 
    { 
        public ulong m_SteamID; 
        public CSteamID(ulong id) { m_SteamID = id; }
        public bool IsValid() => m_SteamID != 0;
        public static CSteamID Nil = new CSteamID(0);
        public static bool operator ==(CSteamID a, CSteamID b) => a.m_SteamID == b.m_SteamID;
        public static bool operator !=(CSteamID a, CSteamID b) => a.m_SteamID != b.m_SteamID;
        public override bool Equals(object? obj) => obj is CSteamID other && m_SteamID == other.m_SteamID;
        public override int GetHashCode() => m_SteamID.GetHashCode();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGameID 
    { 
        public ulong m_GameID; 
        public CGameID(ulong id) { m_GameID = id; }
        public static bool operator ==(CGameID a, CGameID b) => a.m_GameID == b.m_GameID;
        public static bool operator !=(CGameID a, CGameID b) => a.m_GameID != b.m_GameID;
        public override bool Equals(object? obj) => obj is CGameID other && m_GameID == other.m_GameID;
        public override int GetHashCode() => m_GameID.GetHashCode();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PublishedFileId_t { public ulong m_PublishedFileId; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SteamNetworkingIdentity { public void SetSteamID(CSteamID steamID) {} public void SetSteamID64(ulong steamID) {} public CSteamID GetSteamID() => CSteamID.Nil; public ulong GetSteamID64() => 0; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SteamNetworkingMessage_t { public IntPtr m_pData; public int m_cbSize; public int m_nChannel; public int m_nFlags; public SteamNetworkingIdentity m_identityPeer; public void Release() {} }

    [StructLayout(LayoutKind.Sequential)]
    public struct SteamNetConnectionInfo_t { public ESteamNetworkingConnectionState m_eState; public ESteamNetConnectionEnd m_eEndReason; public string m_szEndDebug => ""; public SteamNetworkingIdentity m_identityRemote; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FriendGameInfo_t { public CGameID m_gameID; public CSteamID m_steamIDLobby; }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputAnalogActionData_t { public float x; public float y; public bool bActive; }

    [StructLayout(LayoutKind.Sequential)]
    public struct InputDigitalActionData_t { public bool bState; public bool bActive; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LeaderboardEntry_t { public CSteamID m_steamIDUser; public int m_nGlobalRank; public int m_nScore; public int m_cDetails; }

    // --- Callback Structs ---
    public struct GameLobbyJoinRequested_t { public CSteamID m_steamIDLobby; public CSteamID m_steamIDFriend; }
    public struct GameOverlayActivated_t { public byte m_bActive; }
    public struct ItemInstalled_t { public AppId_t m_unAppID; public PublishedFileId_t m_nPublishedFileId; }
    public struct UserStatsReceived_t { public ulong m_nGameID; public EResult m_eResult; public CSteamID m_steamIDUser; }
    public struct GlobalStatsReceived_t { public ulong m_nGameID; public EResult m_eResult; }
    public struct SteamNetConnectionStatusChangedCallback_t { public uint m_hConn; public SteamNetConnectionInfo_t m_info; public ESteamNetworkingConnectionState m_eOldState; }

    // --- Generic Shells ---
    public class Callback<T> : IDisposable
    {
        public delegate void DispatchDelegate(T param);
        public static Callback<T> Create(DispatchDelegate func) => new Callback<T>();
        public void Dispose() {}
        public Callback(DispatchDelegate func, bool bGameServer = false) {}
        private Callback() {}
    }

    public class CallResult<T> : IDisposable
    {
        public delegate void APIDispatchDelegate(T param, bool bIOFailure);
        public static CallResult<T> Create(APIDispatchDelegate? func = null) => new CallResult<T>();
        public void Set(ulong hAPICall, APIDispatchDelegate? func = null) {}
        public void Cancel() {}
        public void Dispose() {}
    }

    // --- API Classes ---
    public static class SteamAPI
    {
        public static ESteamAPIInitResult InitEx(out string outErrMsg) { outErrMsg = ""; return ESteamAPIInitResult.OK; }
        public static bool IsSteamRunning() => false;
        public static void Shutdown() {}
        public static void RunCallbacks() {}
    }

    public static class SteamApps
    {
        public static string GetCurrentBetaName() => "";
    }

    public static class SteamFriends
    {
        public static string GetPersonaName() => "Player";
        public static string GetFriendPersonaName(CSteamID steamID) => "Friend";
        public static void SetRichPresence(string pchKey, string pchValue) {}
        public static void ClearRichPresence() {}
        public static int GetFriendCount(int iFriendFlags) => 0;
        public static CSteamID GetFriendByIndex(int iFriend, int iFriendFlags) => CSteamID.Nil;
        public static bool GetFriendGamePlayed(CSteamID steamIDFriend, out FriendGameInfo_t pFriendGameInfo) { pFriendGameInfo = default; return false; }
        public static void ActivateGameOverlayToWebPage(string pchURL) {}
        public static void ActivateGameOverlayInviteDialog(CSteamID steamIDLobby) {}
    }

    public static class SteamInput
    {
        public static void RunFrame() {}
        public static int GetConnectedControllers(uint[] handlesOut) => 0;
        public static EInputType GetInputTypeForHandle(uint inputHandle) => EInputType.k_EDeviceType_Unknown;
        public static ulong GetActionSetHandle(string pszActionSetName) => 0;
        public static void ActivateActionSet(uint inputHandle, ulong actionSetHandle) {}
        public static ulong GetDigitalActionHandle(string pszActionName) => 0;
        public static InputDigitalActionData_t GetDigitalActionData(uint inputHandle, ulong digitalActionHandle) => default;
        public static InputAnalogActionData_t GetAnalogActionData(uint inputHandle, ulong analogActionHandle) => default;
        public static int GetDigitalActionOrigins(uint inputHandle, ulong actionSetHandle, ulong digitalActionHandle, EInputActionOrigin[] originsOut) => 0;
        public static string GetGlyphSVGForActionOrigin(EInputActionOrigin eOrigin, uint flags) => "";
        public static string TranslateActionOrigin(EInputActionOrigin eOrigin) => "";
    }

    public static class SteamMatchmaking
    {
        public static int GetNumLobbyMembers(CSteamID steamIDLobby) => 0;
        public static CSteamID GetLobbyMemberByIndex(CSteamID steamIDLobby, int iMember) => CSteamID.Nil;
        public static void SetLobbyType(CSteamID steamIDLobby, int eLobbyType) {}
        public static void LeaveLobby(CSteamID steamIDLobby) {}
    }

    public static class SteamNetworkingSockets
    {
        public static bool AcceptConnection(uint hConn) => false;
        public static bool CloseConnection(uint hPeer, int nReason, string pszDebug, bool bEnableLinger) => false;
        public static bool CloseListenSocket(uint hSocket) => false;
        public static int ReceiveMessagesOnConnection(uint hConn, IntPtr[] ppOutMessages, int nMaxMessages) => 0;
        public static EResult SendMessageToConnection(uint hConn, IntPtr pData, uint cbData, int nSendFlags, out long pOutMessageNumber) { pOutMessageNumber = 0; return EResult.k_EResultFail; }
    }

    public static class SteamNetworkingUtils
    {
        public static void InitRelayNetworkAccess() {}
    }

    public static class SteamRemoteStorage
    {
        public static bool FileWrite(string pchFile, byte[] pvData, int cubData) => true;
        public static int FileRead(string pchFile, byte[] pvData, int cubDataToRead) => 0;
        public static bool FileDelete(string pchFile) => true;
        public static bool FileExists(string pchFile) => false;
        public static bool FileForget(string pchFile) => true;
        public static bool FilePersisted(string pchFile) => false;
        public static int GetFileCount() => 0;
        public static string GetFileNameAndSize(int iFile, out int pnFileSizeInBytes) { pnFileSizeInBytes = 0; return ""; }
        public static int GetFileSize(string pchFile) => 0;
        public static long GetFileTimestamp(string pchFile) => 0;
        public static bool IsCloudEnabledForAccount() => false;
        public static bool IsCloudEnabledForApp() => false;
        public static void BeginFileWriteBatch() {}
        public static void EndFileWriteBatch() {}
    }

    public static class SteamUGC
    {
        public static uint GetNumSubscribedItems() => 0;
        public static uint GetSubscribedItems(PublishedFileId_t[] pvecPublishedFileID, uint cMaxItems) => 0;
        public static bool GetItemInstallInfo(PublishedFileId_t nPublishedFileId, out ulong punSizeOnDisk, out string pchFolder, uint cchFolderSize, out uint punTimeStamp) { punSizeOnDisk = 0; pchFolder = ""; punTimeStamp = 0; return false; }
    }

    public static class SteamUser
    {
        public static CSteamID GetSteamID() => new CSteamID(12345);
    }

    public static class SteamUserStats
    {
        public static bool RequestCurrentStats() => true;
        public static bool GetStat(string pchName, out int pData) { pData = 0; return false; }
        public static bool SetStat(string pchName, int nData) => true;
        public static bool StoreStats() => true;
        public static bool GetGlobalStat(string pchName, out long pData) { pData = 0; return false; }
        public static int GetLeaderboardEntryCount(ulong hSteamLeaderboard) => 0;
    }

    public static class SteamUtils
    {
        public static string GetSteamUILanguage() => "english";
        public static bool IsOverlayEnabled() => false;
        public static bool IsSteamInBigPictureMode() => false;
        public static bool IsSteamRunningOnSteamDeck() => false;
        public static void ShowFloatingGamepadTextInput(int eTextFieldMode, int nTextFieldXPosition, int nTextFieldYPosition, int nTextFieldWidth, int nTextFieldHeight) {}
        public static bool DismissFloatingGamepadTextInput() => true;
    }
}
