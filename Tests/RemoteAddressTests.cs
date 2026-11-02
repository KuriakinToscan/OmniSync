using System;
using System.IO;
using OmniSync.Services;
using OmniSync.Services.FileSystems;
using Xunit;

namespace OmniSync.Tests;

/// <summary>
/// Testes do endereço <c>sftp://</c> e das regras de caminho com origem ou destino remotos.
/// </summary>
public class RemoteAddressTests
{
    /// <summary>Leitura e forma canônica.</summary>
    [Theory]
    [InlineData("sftp://ana@NAS.local/dados/campo", "ana", "nas.local", 22, "/dados/campo", "sftp://ana@nas.local/dados/campo")]
    [InlineData("  sftp://ana@nas:2222/dados/campo/  ", "ana", "nas", 2222, "/dados/campo", "sftp://ana@nas:2222/dados/campo")]
    [InlineData("SFTP://ana@nas", "ana", "nas", 22, "/", "sftp://ana@nas/")]
    [InlineData("sftp://ana@nas:22//dados//./x", "ana", "nas", 22, "/dados/x", "sftp://ana@nas/dados/x")]
    [InlineData("sftp://ana@[::1]:2200/C:/Users", "ana", "::1", 2200, "/C:/Users", "sftp://ana@[::1]:2200/C:/Users")]
    [InlineData("sftp://dom%5Cana@srv/p", "dom\\ana", "srv", 22, "/p", "sftp://dom\\ana@srv/p")]
    public void Parse_Canonical(string text, string user, string host, int port, string path, string canonical)
    {
        var address = RemoteAddress.Parse(text);
        Assert.Equal(user, address.User);
        Assert.Equal(host, address.Host);
        Assert.Equal(port, address.Port);
        Assert.Equal(path, address.Path);
        Assert.Equal(canonical, address.ToString());
        Assert.Equal(address, RemoteAddress.Parse(address.ToString()));
    }

    /// <summary>Endereços recusados, com mensagem para o usuário.</summary>
    [Theory]
    [InlineData("sftp://nas/dados")]
    [InlineData("sftp://ana@/dados")]
    [InlineData("sftp://ana@nas:0/dados")]
    [InlineData("sftp://ana@nas:abc/dados")]
    [InlineData("sftp://ana@nas/dados/../etc")]
    [InlineData("C:\\dados")]
    public void TryParse_Rejects(string text)
    {
        Assert.False(RemoteAddress.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>Chaves de conexão e de servidor.</summary>
    [Fact]
    public void Keys()
    {
        var address = RemoteAddress.Parse("sftp://ana@Nas:2222/x");
        Assert.Equal("ana@nas:2222", address.EndpointKey);
        Assert.Equal("nas:2222", address.HostPortKey);
        Assert.Equal("x", address.Leaf);
        Assert.Equal(string.Empty, address.WithPath("/").Leaf);
        Assert.True(address.SameEndpoint(RemoteAddress.Parse("sftp://ana@nas:2222/outra")));
        Assert.False(address.SameEndpoint(RemoteAddress.Parse("sftp://bia@nas:2222/x")));
    }

    /// <summary>Subpasta com o nome da origem quando o destino é remoto ou a origem é remota.</summary>
    [Fact]
    public void EffectiveDestination_Remote()
    {
        var local = Path.Combine(Path.GetTempPath(), "Campo");
        Assert.Equal("sftp://ana@nas/backup/Campo", PathHelper.ResolveEffectiveDestination(local, "sftp://ana@nas/backup", true));
        Assert.Equal("sftp://ana@nas/backup", PathHelper.ResolveEffectiveDestination(local, "sftp://ana@nas/backup", false));
        Assert.Equal("sftp://ana@nas/Campo", PathHelper.ResolveEffectiveDestination(local, "sftp://ana@nas/", false));
        Assert.Equal("sftp://ana@nas/backup/Campo", PathHelper.ResolveEffectiveDestination(local, "sftp://ana@nas/backup/Campo", true));
        var backup = Path.Combine(Path.GetTempPath(), "Backup");
        Assert.Equal(Path.Combine(backup, "levantamentos"),
            PathHelper.ResolveEffectiveDestination("sftp://ana@nas/dados/levantamentos", backup, true));
    }

    /// <summary>Mesma pasta e pastas aninhadas no remoto; local e remoto nunca se contêm.</summary>
    [Fact]
    public void PairProblem_Remote()
    {
        Assert.NotNull(PathHelper.DescribePairProblem("sftp://ana@nas/dados", "sftp://ana@NAS:22/dados/"));
        Assert.NotNull(PathHelper.DescribePairProblem("sftp://ana@nas/dados", "sftp://ana@nas/dados/copia"));
        Assert.NotNull(PathHelper.DescribePairProblem("sftp://ana@nas/dados/sub", "sftp://ana@nas/dados"));
        Assert.Null(PathHelper.DescribePairProblem("sftp://ana@nas/dados", "sftp://ana@nas/dados2"));
        Assert.Null(PathHelper.DescribePairProblem("sftp://ana@nas/dados", "sftp://ana@outro/dados"));
        Assert.Null(PathHelper.DescribePairProblem(Path.GetTempPath(), "sftp://ana@nas/tmp"));
        Assert.Equal("sftp://ana@nas/x", PathHelper.ExpandUserPath("  sftp://ana@nas/x "));
    }
}
