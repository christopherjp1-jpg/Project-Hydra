using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class AcreditacionDocumentoPlataformaRepositorioFalso : IAcreditacionDocumentoPlataformaRepository
{
    public List<AcreditacionDocumentoPlataforma> Acreditaciones { get; } = [];

    public Task<AcreditacionDocumentoPlataforma?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Acreditaciones.FirstOrDefault(a => a.Id == id));

    public Task<IReadOnlyList<AcreditacionDocumentoPlataforma>> ObtenerPorDocumentoIdAsync(
        Guid documentoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AcreditacionDocumentoPlataforma>>(
            Acreditaciones.Where(a => a.DocumentoId == documentoId).ToList());

    public void Agregar(AcreditacionDocumentoPlataforma acreditacion) => Acreditaciones.Add(acreditacion);
}
