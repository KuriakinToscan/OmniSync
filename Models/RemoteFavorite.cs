using System;

namespace OmniSync.Models;

/// <summary>
/// Endereço remoto com nome, escolhido nos campos de origem e destino. Nunca guarda senha nem chave privada:
/// a senha (ou a frase-senha da chave) fica no cofre do sistema.
/// </summary>
public sealed class RemoteFavorite
{
    /// <summary>Identificador estável.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Nome exibido (ex.: "NAS do setor").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Endereço <c>sftp://usuario@servidor[:porta]/pasta</c>.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Arquivo de chave privada (ex.: <c>id_ed25519</c>); vazio para entrar com senha.</summary>
    public string? KeyFile { get; set; }
}
