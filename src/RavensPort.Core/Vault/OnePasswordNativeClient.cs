using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RavensPort.Core.Vault;

public static partial class OnePasswordNativeClient
{
    private const string DllName = "onepassword.dll";

    // LibraryImport rather than DllImport, so the marshalling is generated at compile time instead
    // of being built by the runtime on first call. The reason it matters here is the string
    // encoding: every one of these takes UTF-8, because the other side is Go, and DllImport had to
    // be told that a parameter at a time with [MarshalAs(UnmanagedType.LPUTF8Str)]. Stating it once
    // per import removes fourteen chances to forget one, and forgetting one does not fail to
    // compile — it silently hands Go a UTF-16 buffer.
    //
    // Cdecl is spelled out even though it is the same ABI as stdcall on x64, which is the only
    // architecture this DLL is built for: it is what the Go side declares, and an ARM64 build
    // would care.

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr InitializeOP(string accountName);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr InitializeOPServiceAccount(string token);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr VaultList();

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr VaultCreate(string name, string description);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr ItemList(string vaultId);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr ItemGet(string vaultId, string itemId);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr ItemCreate(string vaultId, string itemJson);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr ItemEdit(string vaultId, string itemId, string itemJson);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr ItemDelete(string vaultId, string itemId);

    [LibraryImport(DllName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void FreeString(IntPtr ptr);

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
