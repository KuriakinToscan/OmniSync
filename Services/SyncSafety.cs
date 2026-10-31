namespace OmniSync.Services;

/// <summary>
/// Regras de segurança compartilhadas pela interface e pelo modo de linha de comando.
/// </summary>
public static class SyncSafety
{
    /// <summary>
    /// Indica remoção em massa: muitos órfãos em relação ao destino, sinal típico de destino escolhido errado.
    /// Critério: pelo menos 1.000 órfãos, ou pelo menos 10 órfãos representando 20% ou mais dos arquivos do destino.
    /// </summary>
    /// <param name="orphanCount">Órfãos que iriam para <c>lixo/</c>.</param>
    /// <param name="destinationFileCount">Total de arquivos indexados no destino.</param>
    public static bool IsMassOrphan(int orphanCount, int destinationFileCount) =>
        orphanCount >= 1000 ||
        (orphanCount >= 10 && destinationFileCount > 0 && orphanCount * 5 >= destinationFileCount);
}
