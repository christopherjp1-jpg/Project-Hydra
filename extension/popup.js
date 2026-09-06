// UI del popup. No habla con Hydra directamente: todo pasa por mensajes al
// service worker (background.js), que es quien guarda el token y hace fetch.

const seccionConectar = document.getElementById("seccion-conectar");
const seccionConectado = document.getElementById("seccion-conectado");
const campoUrl = document.getElementById("campo-url");
const campoToken = document.getElementById("campo-token");
const botonConectar = document.getElementById("boton-conectar");
const errorConectar = document.getElementById("error-conectar");
const textoUrl = document.getElementById("texto-url");
const textoExpira = document.getElementById("texto-expira");
const botonDesconectar = document.getElementById("boton-desconectar");
const botonActualizar = document.getElementById("boton-actualizar");
const errorLista = document.getElementById("error-lista");
const vacioLista = document.getElementById("vacio-lista");
const listaProveedores = document.getElementById("lista-proveedores");

function enviarMensaje(mensaje) {
  return chrome.runtime.sendMessage(mensaje);
}

function mostrarError(elemento, texto) {
  elemento.textContent = texto;
  elemento.hidden = !texto;
}

async function inicializar() {
  const conexion = await enviarMensaje({ accion: "obtenerConexion" });

  seccionConectar.hidden = conexion.conectado;
  seccionConectado.hidden = !conexion.conectado;

  if (conexion.conectado) {
    textoUrl.textContent = conexion.hydraUrl;
    textoExpira.textContent = new Date(conexion.expiraEnUtc).toLocaleTimeString("es-ES", {
      hour: "2-digit",
      minute: "2-digit",
    });
    await cargarPendientesAsync();
  }
}

botonConectar.addEventListener("click", async () => {
  mostrarError(errorConectar, "");
  const hydraUrl = campoUrl.value.trim();
  const token = campoToken.value.trim();

  if (!hydraUrl || !token) {
    mostrarError(errorConectar, "Completa la URL y el token.");
    return;
  }

  botonConectar.disabled = true;
  try {
    // Vigencia informativa: la caducidad real la impone y comprueba el
    // servidor en cada petición (ver TokenExtensionRespuesta.ExpiraEnUtc en
    // el backend) — aquí solo decide cuándo el popup deja de dar el token
    // por bueno sin haber hecho ninguna llamada todavía.
    const expiraEnUtc = new Date(Date.now() + 8 * 60 * 60 * 1000).toISOString();
    const resultado = await enviarMensaje({ accion: "conectar", hydraUrl, token, expiraEnUtc });

    if (!resultado.ok) {
      mostrarError(errorConectar, resultado.error);
      return;
    }

    campoToken.value = "";
    await inicializar();
  } finally {
    botonConectar.disabled = false;
  }
});

botonDesconectar.addEventListener("click", async () => {
  await enviarMensaje({ accion: "desconectar" });
  await inicializar();
});

botonActualizar.addEventListener("click", cargarPendientesAsync);

async function cargarPendientesAsync() {
  mostrarError(errorLista, "");
  vacioLista.hidden = true;
  listaProveedores.innerHTML = "";
  botonActualizar.disabled = true;

  try {
    const resultado = await enviarMensaje({ accion: "listarPendientes" });

    if (!resultado.ok) {
      mostrarError(errorLista, resultado.error);
      return;
    }

    if (resultado.proveedores.length === 0) {
      vacioLista.hidden = false;
      return;
    }

    for (const proveedor of resultado.proveedores) renderizarProveedor(proveedor);
  } finally {
    botonActualizar.disabled = false;
  }
}

function renderizarProveedor(proveedor) {
  const detalle = document.createElement("details");
  detalle.open = true;

  const resumen = document.createElement("summary");
  const totalDocumentos = proveedor.clientes.reduce((suma, c) => suma + c.documentos.length, 0);
  resumen.textContent = `${proveedor.proveedorNombre} (${totalDocumentos})`;
  detalle.appendChild(resumen);

  for (const cliente of proveedor.clientes) {
    const grupoCliente = document.createElement("div");
    grupoCliente.className = "grupo-cliente";

    const tituloCliente = document.createElement("p");
    tituloCliente.className = "titulo-cliente";
    tituloCliente.textContent = cliente.clienteNombre;
    grupoCliente.appendChild(tituloCliente);

    for (const documento of cliente.documentos) grupoCliente.appendChild(renderizarDocumento(documento));

    detalle.appendChild(grupoCliente);
  }

  listaProveedores.appendChild(detalle);
}

function renderizarDocumento(documento) {
  const fila = document.createElement("div");
  fila.className = "fila-documento";

  const descripcion = document.createElement("span");
  descripcion.textContent = `${documento.propietarioNombre} — ${documento.tipoDocumentoNombre}`;
  // El backend serializa el enum por nombre (JsonStringEnumConverter, ver
  // Program.cs), nunca por su valor ordinal.
  if (documento.estado === "Rechazada") {
    descripcion.textContent += " (rechazada antes)";
  }
  fila.appendChild(descripcion);

  const boton = document.createElement("button");
  boton.type = "button";
  boton.textContent = "Subir";
  boton.addEventListener("click", () => subirAsync(documento, boton, fila));
  fila.appendChild(boton);

  return fila;
}

async function subirAsync(documento, boton, fila) {
  boton.disabled = true;
  boton.textContent = "Subiendo…";

  const resultado = await enviarMensaje({
    accion: "subirDocumento",
    documentoId: documento.documentoId,
    acreditacionId: documento.acreditacionId,
    nombreArchivo: `${documento.tipoDocumentoNombre}.pdf`,
  });

  if (!resultado.ok) {
    boton.disabled = false;
    boton.textContent = "Subir";
    const aviso = document.createElement("p");
    aviso.className = "error";
    aviso.textContent = resultado.error;
    fila.appendChild(aviso);
    return;
  }

  fila.classList.add("fila-completada");
  boton.textContent = "Subido";
  await cargarPendientesAsync();
}

inicializar();
