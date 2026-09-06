// Content script inyectado en las plataformas CAE externas declaradas en
// manifest.json (ver ARQUITECTURA-INTEGRACIONES.md § 14.1, repositorio de
// negocio, para el mecanismo completo). Único trabajo: recibir un fichero en
// base64 del service worker y depositarlo en el input de la plataforma —
// nunca decide cuándo subir nada, eso lo dispara el gestor desde el popup.

// Recuerda el último <input type="file"> que el usuario tocó en esta página:
// con formularios de varias filas (uno por tipo de documento), asumir "el
// primero que haya" sería casi siempre el input equivocado. Si el gestor
// no llegó a hacer foco en ninguno, se cae al primero que exista en el DOM
// como último recurso, documentado como limitación conocida (riesgo ya
// registrado en el diseño: cada plataforma puede necesitar su propio
// selector, todavía sin verificar una por una).
let ultimoInputArchivoTocado = null;

document.addEventListener(
  "focusin",
  (evento) => {
    if (evento.target instanceof HTMLInputElement && evento.target.type === "file")
      ultimoInputArchivoTocado = evento.target;
  },
  true
);

function base64AArchivo(base64, nombreArchivo, tipoMime) {
  const binario = atob(base64);
  const bytes = new Uint8Array(binario.length);
  for (let i = 0; i < binario.length; i++) bytes[i] = binario.charCodeAt(i);
  return new File([bytes], nombreArchivo, { type: tipoMime });
}

function inyectarEnInput(input, archivo) {
  // El truco DataTransfer, no una asignación directa a `.files` (esa
  // propiedad no tiene setter propio salvo el que los navegadores exponen
  // específicamente para este caso, compatible con la semántica de
  // arrastrar-y-soltar). Un input controlado por React puede no reaccionar a
  // esto en todas las plataformas — riesgo ya documentado, no resuelto aquí
  // por plataforma.
  const transferencia = new DataTransfer();
  transferencia.items.add(archivo);
  input.files = transferencia.files;

  input.dispatchEvent(new Event("input", { bubbles: true }));
  input.dispatchEvent(new Event("change", { bubbles: true }));
}

chrome.runtime.onMessage.addListener((mensaje, _remitente, enviarRespuesta) => {
  if (mensaje?.accion !== "inyectarArchivo") return false;

  try {
    const input =
      ultimoInputArchivoTocado ?? document.querySelector('input[type="file"]');

    if (!input) {
      enviarRespuesta({ ok: false, error: "No encontramos ningún campo de subida de archivo en esta página." });
      return true;
    }

    const archivo = base64AArchivo(mensaje.base64, mensaje.nombreArchivo, mensaje.tipoMime);
    inyectarEnInput(input, archivo);

    enviarRespuesta({ ok: true });
  } catch (error) {
    enviarRespuesta({ ok: false, error: `No pudimos rellenar el formulario (${error.message}).` });
  }

  return true;
});
