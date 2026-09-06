// Service worker (MV3) del MVP1 de integración con plataformas CAE externas.
// Ver ARQUITECTURA-INTEGRACIONES.md § 14 (repositorio de negocio) para el
// diseño completo. Este fichero es el único que guarda el token y habla con
// Hydra; el popup y el content script solo le mandan mensajes.
//
// Estado guardado:
//   chrome.storage.local:   { hydraUrl }               -- sobrevive a reinicios
//   chrome.storage.session: { token, expiraEnUtc }      -- se pierde al cerrar el navegador,
//                                                           igual de corto que la vigencia real (8h)
//                                                           que de todos modos impone el servidor.

async function obtenerConexion() {
  const { hydraUrl } = await chrome.storage.local.get("hydraUrl");
  const { token, expiraEnUtc } = await chrome.storage.session.get(["token", "expiraEnUtc"]);
  const conectado = Boolean(hydraUrl && token && expiraEnUtc && new Date(expiraEnUtc) > new Date());
  return { hydraUrl: hydraUrl ?? null, token: token ?? null, expiraEnUtc: expiraEnUtc ?? null, conectado };
}

function normalizarOrigen(url) {
  try {
    return new URL(url).origin;
  } catch {
    return null;
  }
}

async function conectar(hydraUrl, token, expiraEnUtc) {
  const origen = normalizarOrigen(hydraUrl);
  if (!origen) return { ok: false, error: "La URL de Hydra no es válida." };

  // El origen se pide en el momento de conectar, no se declara por adelantado
  // en el manifest: cada cliente de Hydra vive en un dominio propio y la
  // extensión no puede conocerlos todos de antemano (ver optional_host_permissions
  // en manifest.json).
  const concedido = await chrome.permissions.request({ origins: [`${origen}/*`] });
  if (!concedido) return { ok: false, error: "Sin permiso sobre ese dominio no se puede llamar a Hydra." };

  await chrome.storage.local.set({ hydraUrl: origen });
  await chrome.storage.session.set({ token, expiraEnUtc });
  return { ok: true };
}

async function desconectar() {
  await chrome.storage.session.remove(["token", "expiraEnUtc"]);
  return { ok: true };
}

async function peticionAutenticada(ruta, opciones = {}) {
  const { hydraUrl, token, conectado } = await obtenerConexion();
  if (!conectado) return { ok: false, error: "No hay una conexión activa con Hydra. Vuelve a conectar." };

  let respuesta;
  try {
    respuesta = await fetch(`${hydraUrl}${ruta}`, {
      ...opciones,
      // "manual": estos endpoints están protegidos con una política que
      // combina el esquema de extensión con el de cookie de Identity
      // (Policies.SesionOExtension). Cuando ninguno de los dos autentica, el
      // reto que gana es el de la cookie: una redirección 302 a la pantalla
      // de login, no un 401 limpio (comprobado contra el servidor real, no
      // supuesto). Con el "follow" por defecto, fetch sigue esa redirección
      // sola, entrega la página de login con status 200, y el .json() de más
      // abajo lanzaría una excepción sin control al intentar parsear HTML.
      // "manual" deja la redirección sin seguir para poder tratarla aquí como
      // lo que es: sesión/token inválidos, igual que un 401 de verdad.
      redirect: "manual",
      headers: { ...(opciones.headers ?? {}), Authorization: `Extension ${token}` },
    });
  } catch (error) {
    return { ok: false, error: `No pudimos contactar con Hydra (${error.message}).` };
  }

  if (respuesta.status === 401 || respuesta.type === "opaqueredirect") {
    // Mismo criterio que el servidor (ExtensionAuthenticationHandler): un
    // token caducado o revocado no distingue el motivo. Se limpia aquí para
    // que el popup vuelva a pedir conexión en vez de seguir fallando en bucle.
    await desconectar();
    return { ok: false, error: "Tu conexión con Hydra caducó. Vuelve a conectar." };
  }

  return { ok: true, respuesta };
}

async function listarPendientes() {
  const resultado = await peticionAutenticada("/extension/acreditaciones-pendientes");
  if (!resultado.ok) return resultado;

  if (!resultado.respuesta.ok)
    return { ok: false, error: `Hydra respondió ${resultado.respuesta.status}.` };

  return { ok: true, proveedores: await resultado.respuesta.json() };
}

function arrayBufferABase64(buffer) {
  let binario = "";
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.byteLength; i++) binario += String.fromCharCode(bytes[i]);
  return btoa(binario);
}

async function subirDocumento({ documentoId, acreditacionId, nombreArchivo }) {
  const descarga = await peticionAutenticada(`/documentos/${documentoId}/archivo`);
  if (!descarga.ok) return descarga;
  if (!descarga.respuesta.ok) return { ok: false, error: `No pudimos descargar el PDF de Hydra (${descarga.respuesta.status}).` };

  const base64 = arrayBufferABase64(await descarga.respuesta.arrayBuffer());

  const [pestana] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!pestana?.id) return { ok: false, error: "No hay ninguna pestaña activa donde inyectar el archivo." };

  let respuestaContenido;
  try {
    respuestaContenido = await chrome.tabs.sendMessage(pestana.id, {
      accion: "inyectarArchivo",
      base64,
      nombreArchivo: nombreArchivo || "documento.pdf",
      tipoMime: "application/pdf",
    });
  } catch {
    return {
      ok: false,
      error: "Esta pestaña no tiene el content script cargado (¿es una plataforma CAE reconocida? ¿recargaste la página tras instalar la extensión?).",
    };
  }

  if (!respuestaContenido?.ok)
    return { ok: false, error: respuestaContenido?.error ?? "No pudimos rellenar el formulario de la plataforma." };

  // Marcar "subida" es un acto humano confirmando que el formulario se rellenó
  // — no que la plataforma ya lo aceptó (eso lo revisa el gestor manualmente
  // y lo registra con "Marcar aceptado"/"Marcar rechazado" en Hydra).
  const marcado = await peticionAutenticada(`/extension/acreditaciones/${acreditacionId}/subida`, { method: "POST" });
  if (!marcado.ok) return marcado;
  if (!marcado.respuesta.ok) return { ok: false, error: `Hydra no aceptó marcarla como subida (${marcado.respuesta.status}).` };

  return { ok: true };
}

chrome.runtime.onMessage.addListener((mensaje, _remitente, enviarRespuesta) => {
  const manejadores = {
    obtenerConexion: () => obtenerConexion(),
    conectar: (m) => conectar(m.hydraUrl, m.token, m.expiraEnUtc),
    desconectar: () => desconectar(),
    listarPendientes: () => listarPendientes(),
    subirDocumento: (m) => subirDocumento(m),
  };

  const manejador = manejadores[mensaje?.accion];
  if (!manejador) return false;

  manejador(mensaje).then(enviarRespuesta);
  return true; // respuesta asíncrona
});
