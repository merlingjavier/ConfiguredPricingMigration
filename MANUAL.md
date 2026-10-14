# Manual de Usuario: Migracion de Configured Pricing

## 1. Objetivo

Esta aplicacion migra tarifarios de credito desde SQL Server hacia la base operativa de Configured Pricing. La migracion es directa: sincroniza los catalogos requeridos, crea una version inmutable del tarifario y la publica al finalizar cada alcance valido.

No requiere una base de staging ni un Run ID para ejecutar la migracion.

## 2. A quien esta dirigido

Este manual esta dirigido al equipo autorizado para ejecutar y revisar la migracion de tarifarios. Soporte tecnico o el administrador de bases de datos prepara previamente las conexiones a SQL Server y MongoDB. Las credenciales no se muestran ni se editan desde la aplicacion.

## 3. Antes de iniciar

Verifique lo siguiente:

- Cuenta con autorizacion para ejecutar la migracion.
- La configuracion tecnica fue preparada por soporte.
- Tiene conectividad de red o VPN hacia SQL Server y MongoDB.
- Conoce el tipo de esquema que debe migrar.
- Si migrara productos Ordinarios especificos, dispone de sus IDs.
- No hay otra migracion sobre el mismo alcance ejecutandose sin coordinacion.

Al abrir la aplicacion, revise el mensaje de la cabecera:

- **Configuracion detectada**: puede continuar con la prueba de conexiones.
- **Configuracion pendiente**: solicite a soporte la revision de las variables de entorno o del archivo `.env`.

## 4. Tipos de esquema

| Tipo | Cuando utilizarlo | Alcance |
| --- | --- | --- |
| **Ordinario** | Tarifario completo de uno o varios productos de credito. | Se crea y publica un esquema por producto, con modalidades `STANDARD` y `NEGOTIABLE`. |
| **Linea Base** | Tasas 35, 60 y 62. | Se crea un unico esquema global. No se seleccionan productos. |
| **Especial** | Tasa 64 por monto y establecimiento. | Se crea un unico esquema global. No se seleccionan productos. |

Importante:

- **Productos (opcional)** se usa solo para Ordinario.
- En Ordinario, deje el campo vacio para identificar automaticamente los productos elegibles.
- Para limitar el alcance, ingrese IDs positivos separados por coma, por ejemplo: `1028,1035,1041`.
- En Linea Base y Especial no ingrese productos.

## 5. Preparar la migracion

1. Abra **Migracion de Configured Pricing**.
2. Seleccione el tipo de esquema autorizado.
3. Si aplica, ingrese los IDs de producto.
4. Revise el **Tamano de lote**. El valor recomendado es `10000`; se admiten valores entre `1000` y `100000`.
5. Seleccione **Probar conexiones**.

No inicie la migracion si la prueba de conexiones presenta errores.

## 6. Ejecutar la migracion

1. Confirme el tipo de esquema y los productos seleccionados.
2. Seleccione **Migrar**.
3. Supervise el panel **Actividad de la migracion**.
4. Espere el mensaje final con filas extraidas, cargadas, rechazadas y conjuntos generados.

La aplicacion sincroniza primero los catalogos vigentes requeridos. Si esta sincronizacion falla, la extraccion no inicia.

Para Ordinario, cada producto valido se escribe, publica y activa antes de procesar el siguiente. Para Linea Base y Especial, el esquema global se activa solo despues de persistir todos sus datos.

La publicacion es automatica al completar la migracion; no existe un paso manual separado de transferencia o publicacion.

## 7. Actividad y consola

El panel de actividad muestra las etapas, registros procesados, cargados, mensajes de resultado y errores. Use **Limpiar consola** para vaciar solo el historial visible de la ventana. Esta accion no elimina datos publicados ni archivos de evidencia.

Use **Detener migracion** solo si necesita interrumpir la ejecucion. Los productos Ordinarios que ya fueron publicados permanecen activos; los productos aun no terminados no se publican.

## 8. Conflictos de tasas

Una celda tarifaria se determina por sus rangos, grupos de dimension, seguro, moneda, tipo de persona y modalidad. Cada celda admite una sola tasa por tipo de tasa.

Si dos o mas filas vigentes de `SI_FinTasa` pertenecen a la misma celda y tipo de tasa, pero sus valores difieren, la aplicacion resuelve el conflicto de esta forma:

1. Conserva la fila con el mayor `idTasa`.
2. Migra los valores de la fila seleccionada.
3. Omite las filas con `idTasa` menor para esa celda y tipo de tasa.
4. Registra la decision completa en un archivo de evidencia.

La regla asume que el mayor `idTasa` corresponde al registro mas reciente. Revise el archivo de evidencia y confirme esta premisa con el responsable de datos cuando existan conflictos.

Un conflicto resuelto no rechaza el producto ni detiene la migracion. Los errores tecnicos, de conectividad o de permisos siguen deteniendo la operacion.

## 9. Archivos de evidencia

Cuando se resuelven conflictos de tasas para productos Ordinarios, la aplicacion genera un archivo:

```text
migration-product-issues-YYYYMMDD-HHMMSS-fff.log
```

El archivo se crea solo si hubo conflictos y contiene:

- Fecha y tipo de esquema.
- Producto afectado.
- Celda y tipo de tasa.
- `idTasa` seleccionado.
- `idTasa` omitidos.
- Valores compensatorios, maximos y moratorios de todas las filas involucradas.

Ubicacion del archivo:

| Ejecucion | Carpeta |
| --- | --- |
| Debug o desarrollo | `<raiz-del-proyecto>\logs\` |
| Release o ejecutable publicado | `<carpeta-del-ejecutable>\logs\` |

El mensaje final de la consola informa la ruta completa del archivo creado. La carpeta `logs` no se crea cuando no hay conflictos.

## 10. Errores frecuentes

| Mensaje o situacion | Accion recomendada |
| --- | --- |
| Configuracion pendiente | Solicite a soporte que revise las variables de entorno o el archivo `.env`. |
| SQL Server no disponible | Verifique red, VPN y disponibilidad del servidor. Luego pruebe las conexiones. |
| Acceso a SQL Server denegado | Solicite la revision de credenciales y permisos sobre la base fuente. |
| MongoDB no disponible | Verifique red, VPN, URI y disponibilidad de MongoDB. |
| Acceso a MongoDB denegado | Solicite los permisos necesarios sobre la base operativa. |
| Tiempo de espera agotado | Espere, revise la conectividad y vuelva a intentar. Si persiste, escale a soporte. |
| Datos de entrada invalidos | Revise el tipo de esquema, productos y tamano de lote. |
| Conflictos resueltos por mayor `idTasa` | Revise el archivo generado en `logs` y confirme que la fila mayor es la tarifa aprobada. |

Cuando aparezca una ventana de error:

1. Anote el codigo de incidente.
2. Revise la recomendacion mostrada.
3. Use **Copiar detalles** si requiere escalar el caso.
4. No incluya credenciales en correos, tickets ni capturas.

## 11. Cierre operativo

Al finalizar, conserve como minimo:

- Tipo de esquema y productos migrados.
- Fecha y hora de la ejecucion.
- Conteos finales mostrados en la consola.
- Archivo de `logs` cuando se resolvieron conflictos.
- Codigo de incidente, si se presento un error.
- Aprobacion operativa correspondiente.

Antes de dar por aprobada una migracion con conflictos resueltos, valide que las tasas seleccionadas por mayor `idTasa` corresponden a la matriz vigente aprobada.

## 12. Recomendaciones

- Ejecute primero una migracion controlada sobre productos especificos cuando el plan de trabajo lo requiera.
- No cierre la aplicacion durante una migracion.
- Revise el archivo de evidencia antes de aprobar una ejecucion con conflictos resueltos.
- Coordine con soporte antes de modificar configuracion tecnica o repetir una migracion con incidencias.
