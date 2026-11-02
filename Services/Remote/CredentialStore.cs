using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OmniSync.Services.Remote;

/// <summary>
/// Cofre de senhas do sistema operacional. Guarda só a senha (ou a frase-senha da chave) de cada conexão remota;
/// o restante do favorito fica no <c>settings.json</c>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Nome do cofre, para mensagens (ex.: "Gerenciador de Credenciais do Windows").</summary>
    string DisplayName { get; }

    /// <summary>Indica se há cofre utilizável nesta sessão.</summary>
    bool IsAvailable { get; }

    /// <summary>Lê o segredo guardado para a conexão, ou <c>null</c> se não houver.</summary>
    string? Read(string key);

    /// <summary>Grava (ou substitui) o segredo. Devolve <c>false</c> se o cofre recusar.</summary>
    bool Write(string key, string secret);

    /// <summary>Apaga o segredo, se existir.</summary>
    void Delete(string key);
}

/// <summary>
/// Escolhe o cofre da plataforma: Gerenciador de Credenciais no Windows e libsecret (<c>secret-tool</c>) no Linux.
/// </summary>
public static class CredentialStore
{
    private const string TargetPrefix = "OmniSync:sftp:";

    private static readonly Lazy<ISecretStore> Platform = new(() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialStore() :
        OperatingSystem.IsLinux() ? new SecretToolStore() :
        new NoSecretStore());

    /// <summary>Cofre em uso; os testes podem trocar.</summary>
    public static ISecretStore Current { get; set; } = new LazyStore();

    /// <summary>Chave do cofre para uma conexão (<c>usuario@servidor:porta</c>).</summary>
    public static string KeyFor(string endpointKey) => TargetPrefix + endpointKey;

    private sealed class LazyStore : ISecretStore
    {
        public string DisplayName => Platform.Value.DisplayName;
        public bool IsAvailable => Platform.Value.IsAvailable;
        public string? Read(string key) => Platform.Value.Read(key);
        public bool Write(string key, string secret) => Platform.Value.Write(key, secret);
        public void Delete(string key) => Platform.Value.Delete(key);
    }
}

/// <summary>Sem cofre: nada é guardado e a senha é pedida a cada uso.</summary>
public sealed class NoSecretStore : ISecretStore
{
    /// <inheritdoc />
    public string DisplayName => "nenhum cofre";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string? Read(string key) => null;

    /// <inheritdoc />
    public bool Write(string key, string secret) => false;

    /// <inheritdoc />
    public void Delete(string key)
    {
    }
}

/// <summary>
/// Gerenciador de Credenciais do Windows (credencial genérica, persistida para o usuário), pela API
/// <c>CredWriteW</c>/<c>CredReadW</c>/<c>CredDeleteW</c> do <c>advapi32</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <inheritdoc />
    public string DisplayName => "Gerenciador de Credenciais do Windows";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string? Read(string key)
    {
        if (!CredRead(key, CredTypeGeneric, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) FileLogService.Write($"[AVISO] Cofre do Windows: leitura falhou ({new Win32Exception(error).Message}).");
            return null;
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <inheritdoc />
    public bool Write(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var target = Marshal.StringToCoTaskMemUni(key);
        var user = Marshal.StringToCoTaskMemUni("OmniSync");
        var blobPointer = Marshal.AllocCoTaskMem(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredPersistLocalMachine,
                UserName = user
            };
            if (CredWrite(ref credential, 0)) return true;
            FileLogService.Write($"[AVISO] Cofre do Windows: gravação falhou ({new Win32Exception(Marshal.GetLastWin32Error()).Message}).");
            return false;
        }
        finally
        {
            // Apaga a cópia da senha na memória não gerenciada antes de liberar
            Marshal.Copy(new byte[blob.Length], 0, blobPointer, blob.Length);
            Marshal.FreeCoTaskMem(blobPointer);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
        }
    }

    /// <inheritdoc />
    public void Delete(string key) => CredDelete(key, CredTypeGeneric, 0);
}

/// <summary>
/// Chaveiro do GNOME/KDE pelo <c>secret-tool</c> (libsecret). Sem o programa ou sem sessão D-Bus, fica indisponível.
/// </summary>
public sealed class SecretToolStore : ISecretStore
{
    private readonly Lazy<bool> _available;

    /// <summary>Cria o acesso ao chaveiro.</summary>
    public SecretToolStore()
    {
        // Sem D-Bus ou sem chaveiro, o lookup também sai com 1, mas escreve o motivo na saída de erro
        _available = new Lazy<bool>(() =>
        {
            int code = Run(out _, out var error, null, "lookup", "service", "omnisync", "account", "__omnisync_probe__");
            return code == 0 || (code == 1 && string.IsNullOrWhiteSpace(error));
        });
    }

    /// <inheritdoc />
    public string DisplayName => "chaveiro do sistema (libsecret)";

    /// <inheritdoc />
    public bool IsAvailable => _available.Value;

    /// <inheritdoc />
    public string? Read(string key)
    {
        if (!IsAvailable) return null;
        return Run(out var output, out _, null, "lookup", "service", "omnisync", "account", key) == 0 ? output : null;
    }

    /// <inheritdoc />
    public bool Write(string key, string secret)
    {
        if (!IsAvailable) return false;
        return Run(out _, out _, secret, "store", "--label=OmniSync " + key, "service", "omnisync", "account", key) == 0;
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        if (IsAvailable) Run(out _, out _, null, "clear", "service", "omnisync", "account", key);
    }

    /// <summary>Roda o <c>secret-tool</c>; devolve o código de saída, ou -1 se não deu para executar.</summary>
    private static int Run(out string output, out string error, string? input, params string[] args)
    {
        output = string.Empty;
        error = string.Empty;
        try
        {
            var info = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process == null) return -1;
            if (input != null) process.StandardInput.Write(input);
            process.StandardInput.Close();
            output = process.StandardOutput.ReadToEnd();
            error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return -1;
            }
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return -1;
        }
    }
}
