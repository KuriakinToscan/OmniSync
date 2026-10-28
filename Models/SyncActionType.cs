namespace OmniSync.Models;

/// <summary>
/// Define a categoria de ação identificada durante a varredura diferencial entre Origem e Destino.
/// </summary>
public enum SyncActionType
{
    /// <summary>
    /// O arquivo existe na Origem e no Destino com o mesmo conteúdo/metadados. Nenhuma cópia necessária.
    /// </summary>
    Identical,

    /// <summary>
    /// O arquivo existe apenas na Origem e ainda não existe no Destino. Deve ser copiado.
    /// </summary>
    New,

    /// <summary>
    /// O arquivo existe no Destino, mas não existe mais na Origem (órfão).
    /// Deve ser movido para a subpasta <c>destino/lixo/</c> preservando sua hierarquia relativa.
    /// </summary>
    OrphanTrash,

    /// <summary>
    /// O arquivo existe em ambos os diretórios com o mesmo caminho relativo, porém com conteúdo ou tamanho diferente.
    /// Requer decisão do usuário (Ignorar ou Substituir movendo o antigo para <c>destino/old/</c>).
    /// </summary>
    Conflict
}

/// <summary>
/// Representa a decisão tomada para resolver um conflito de arquivo entre Origem e Destino.
/// </summary>
public enum ConflictResolution
{
    /// <summary>
    /// A resolução ainda não foi definida e será solicitada ao usuário durante a execução da sincronização.
    /// </summary>
    AskUser,

    /// <summary>
    /// Substituir o arquivo do Destino: move a versão antiga do destino para <c>destino/old/</c>
    /// e copia a nova versão a partir da Origem.
    /// </summary>
    Replace,

    /// <summary>
    /// Ignorar a diferença: mantém o arquivo existente no Destino intacto, sem realizar cópia.
    /// </summary>
    Ignore
}
