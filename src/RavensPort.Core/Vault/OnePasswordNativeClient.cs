using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RavensPort.Core.Vault;

public static class OnePasswordNativeClient
{
    /// <summary>
    /// No extension and no prefix, so the runtime probes for the right artefact per platform:
    /// <c>onepassword.dll</c> on Windows, <c>libonepassword.so</c> on Linux. Both are the same Go
    /// source in src/OnePasswordNative built with <c>-buildmode=c-shared</c>.
    ///
    /// It used to name the DLL outright, which worked only because there was one platform. Naming
    /// the extension would make the Linux build look for a file called <c>onepassword.dll</c>.
    /// </summary>
    private const string DllName = "onepassword";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr InitializeOP([MarshalAs(UnmanagedType.LPUTF8Str)] string accountName);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr InitializeOPServiceAccount([MarshalAs(UnmanagedType.LPUTF8Str)] string token);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VaultList();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr VaultCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string description);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemList([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemGet([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemCreate([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemEdit([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ItemDelete([MarshalAs(UnmanagedType.LPUTF8Str)] string vaultId, [MarshalAs(UnmanagedType.LPUTF8Str)] string itemId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeString(IntPtr ptr);

    private static string? GetStringAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        var str = Marshal.PtrToStringUTF8(ptr);
        FreeString(ptr);
        return str;
    }

    public static void Initialize(string accountName)
    {
        var errPtr = InitializeOP(accountName ?? "");
        var err = GetStringAndFree(errPtr);
        if (!string.IsNullOrEmpty(err))
        {
            throw new InvalidOperationException($"Failed to initialize 1Password SDK: {err}");
        }
    }

    /// <summary>
    /// Connects as a service account. The token crosses the interop boundary as UTF-8 and is never
    /// written down on either side of it — see <see cref="OnePasswordSession"/> for why.
    /// </summary>
    public static void InitializeServiceAccount(string token)
    {
        var errPtr = InitializeOPServiceAccount(token ?? "");
        var err = GetStringAndFree(errPtr);
        if (!string.IsNullOrEmpty(err))
        {
            // The SDK's message is safe to surface: it reports why the token was refused, not the
            // token. Anything that echoed the credential back would end up in the activity log.
            throw new InvalidOperationException(
                $"Failed to connect to 1Password with the service account token: {err}");
        }
    }

    private static JsonNode? ParseResponse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj && obj.ContainsKey("error"))
        {
            throw new VaultCliException(obj["error"]!.GetValue<string>());
        }
        return node;
    }

    public static JsonArray? ListVaults()
    {
        var json = GetStringAndFree(VaultList());
        return ParseResponse(json) as JsonArray;
    }

    public static JsonNode? CreateVault(string name, string description)
    {
        var json = GetStringAndFree(VaultCreate(name, description));
        return ParseResponse(json);
    }

    public static JsonArray? ListItems(string vaultId)
    {
        var json = GetStringAndFree(ItemList(vaultId));
        return ParseResponse(json) as JsonArray;
    }

    public static JsonNode? GetItem(string vaultId, string itemId)
    {
        var json = GetStringAndFree(ItemGet(vaultId, itemId));
        return ParseResponse(json);
    }

    public static JsonNode? CreateItem(string vaultId, string itemJson)
    {
        var json = GetStringAndFree(ItemCreate(vaultId, itemJson));
        return ParseResponse(json);
    }

    public static JsonNode? EditItem(string vaultId, string itemId, string itemJson)
    {
        var json = GetStringAndFree(ItemEdit(vaultId, itemId, itemJson));
        return ParseResponse(json);
    }

    public static void DeleteItem(string vaultId, string itemId)
    {
        var err = GetStringAndFree(ItemDelete(vaultId, itemId));
        if (!string.IsNullOrEmpty(err))
        {
            throw new VaultCliException(err);
        }
    }
}
