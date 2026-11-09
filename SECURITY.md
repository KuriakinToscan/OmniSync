# Segurança

## Como relatar uma falha

Não abra *issue* pública para falhas de segurança. Use o relato privado do GitHub: aba **Security** › **Report a vulnerability** deste repositório. Descreva a versão (`OmniSync --versao`), o sistema, os passos para reproduzir e o efeito.

A resposta é dada pelo mesmo canal. A correção sai numa nova versão, registrada no [CHANGELOG](CHANGELOG.md).

## Versões cobertas

Só a versão mais recente publicada em **Releases** recebe correções.

## Como conferir o executável baixado

Cada release traz o `SHA256SUMS.txt`. No Windows (PowerShell):

```powershell
Get-FileHash .\OmniSync-X.Y.Z-win-x64.exe
```

No Linux:

```bash
sha256sum -c SHA256SUMS.txt --ignore-missing
```

O valor precisa ser igual ao da lista. Baixe o OmniSync só da página de releases deste repositório.
