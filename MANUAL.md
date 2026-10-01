# Manual de Usuario: Migración de Configured Pricing

## 1. Objetivo

Esta aplicacion permite realizar la carga inicial de tarifarios de credito desde la fuente corporativa hacia Configured Pricing de forma controlada.

La aplicacion trabaja en dos etapas:

1. Carga, consolida y valida la informacion en una base de migracion o *staging*.
2. Transfiere y publica en la base operativa solo el resultado de una ejecucion validada.

La publicacion activa las versiones migradas en Configured Pricing. Por este motivo, debe realizarse unicamente despues de revisar la validacion y contar con la autorizacion correspondiente.

## 2. A quien esta dirigido

Este manual esta dirigido al area usuaria responsable de ejecutar, revisar y aprobar la migracion inicial de tarifarios.

Soporte tecnico o el administrador de bases de datos debe preparar previamente las conexiones a SQL Server, a la base de migracion y a la base operativa. Las credenciales no se muestran ni se editan desde la aplicacion.

## 3. Antes de iniciar

Verifique lo siguiente antes de ejecutar una migracion:

- Cuenta con autorizacion para ejecutar la carga y, si corresponde, para publicarla.
- La configuracion tecnica fue preparada por soporte.
- Tiene conectividad de red o VPN hacia los servidores requeridos.
- Conoce el tipo de esquema que se debe migrar.
- Si migrara un esquema Ordinario para productos especificos, dispone de sus IDs.
- No hay otra migracion ejecutandose sobre el mismo alcance sin coordinacion previa.

Al abrir la aplicacion, revise el mensaje de la cabecera:

- **Configuracion detectada**: puede continuar con la prueba de conexiones.
- **Configuracion pendiente**: solicite a soporte la revision de las variables de entorno o del archivo `.env` antes de continuar.

## 4. Conceptos principales

| Concepto | Descripcion |
| --- | --- |
| Run ID | Identificador unico de una ejecucion. Permite consultar su estado, validarla, detenerla o continuarla. |
| Staging | Base no operativa donde se guardan los datos extraidos, controles, excepciones y resultados preliminares. |
| Base operativa | Base utilizada por Configured Pricing. Solo recibe datos mediante la accion de transferencia y publicacion. |
| Validacion | Control que verifica que la carga este completa y que no existan inconsistencias que impidan publicar. |
| Publicacion | Activacion de las versiones migradas en la base operativa. |

## 5. Tipos de esquema

Seleccione un solo tipo de esquema para cada Run ID.

| Tipo | Cuando utilizarlo | Alcance |
| --- | --- | --- |
| **Ordinario** | Para migrar el tarifario completo de uno o varios productos de credito. | Se crea un esquema por producto. Incluye modalidades estandar y negociable. |
| **Linea Base** | Para las tasas 35, 60 y 62. | Se crea un unico esquema global; no se seleccionan productos. |
| **Especial** | Para la tasa 64, definida por monto y establecimiento. | Se crea un unico esquema global; no se seleccionan productos. |

Importante:

- El campo **Productos (opcional)** se usa solo para el esquema Ordinario.
- En Ordinario, deje el campo de productos vacio para que la aplicacion identifique automaticamente los productos vigentes y elegibles.
- Si ingresa productos, use IDs numericos positivos separados por comas, por ejemplo: `1028,1035,1041`.
- En Linea Base y Especial no ingrese productos. La aplicacion procesa el alcance global correspondiente.

## 6. Preparar una migracion

1. Abra la aplicación **Migración de Configured Pricing**.
2. En **Tipo de esquema**, seleccione Ordinario, Línea Base o Especial según la migración autorizada.
3. Revise el valor de **Run ID**.
4. Para crear una ejecucion nueva, mantenga el Run ID generado o seleccione **Generar Run ID**.
5. Para retomar o revisar una ejecucion existente, seleccione **Actualizar Runs** y elija el Run requerido en la lista. La aplicacion ajusta automaticamente el tipo de esquema del Run seleccionado.
6. Si eligio Ordinario y requiere limitar el alcance, ingrese los IDs de producto separados por comas.
7. Revise el **Tamano de lote**. El valor recomendado es `10000`; el rango permitido es de 1000 a 100000.
8. Seleccione **Probar conexiones**.

No inicie la migración si la prueba de conexiones presenta errores.

## 7. Ejecutar la migración

1. Confirme que el tipo de esquema, Run ID y productos, si aplican, son correctos.
2. Seleccione **Ejecutar migración**.
3. Supervise el panel **Actividad de la migración** mientras se procesa la información.
4. Espere el mensaje final que informa las filas extraidas, cargadas, rechazadas y los conjuntos generados.
5. Seleccione **Actualizar Runs** para revisar el estado actualizado de la ejecucion.

Durante la ejecucion, la aplicacion primero sincroniza los catalogos vigentes requeridos en la base operativa. Si esta sincronizacion falla, la extraccion no comenzara.

El registro de actividad muestra, entre otros datos:

- Etapa que se esta ejecutando.
- Registros procesados.
- Registros cargados.
- Registros rechazados.
- Mensajes de resultado o error.

## 8. Detener y retomar una migración

Use **Detener migración** solo si necesita interrumpir la ejecución por una razón operativa.

### Esquema Ordinario

Al detener la migracion:

- Los productos terminados se conservan en el Run parcial.
- La aplicacion genera y muestra un nuevo Run ID para los productos pendientes.
- Para continuar, utilice el nuevo Run ID y seleccione nuevamente **Ejecutar migracion**.

### Esquema Linea Base o Especial

Al detener la migracion:

- El Run parcial queda cancelado y no puede publicarse.
- La aplicacion genera un nuevo Run ID.
- El nuevo Run debe reprocesar el esquema global completo.

No reutilice un Run ID con otro tipo de esquema ni cambie el alcance de productos de un Run Ordinario ya iniciado.

## 9. Validar la carga

La validacion es obligatoria antes de transferir y publicar.

1. Seleccione el Run ID que terminó la carga.
2. Seleccione **Validar carga**.
3. Revise el resultado en el panel de actividad.
4. Confirme que no se reporten duplicados, celdas ambiguas, conflictos de grupos ni productos pendientes.

La validacion verifica, entre otros controles:

- Existencia de filas extraidas desde la fuente.
- Existencia de conjuntos de tasas consolidados.
- Ausencia de productos pendientes.
- Ausencia de tipos de tasa duplicados.
- Ausencia de celdas ambiguas.
- Ausencia de conflictos entre grupos o registros globales.

Si la validacion no es satisfactoria, no publique el Run. Registre el Run ID y el codigo de incidente, y solicite la revision de soporte o del responsable de datos.

## 10. Transferir y publicar

Realice este procedimiento solo cuando la validacion haya sido correcta y exista aprobacion para activar el tarifario.

1. Seleccione el Run ID validado.
2. Verifique una vez mas que corresponde al tipo de esquema y alcance aprobados.
3. Seleccione **Transferir y publicar Run validado**.
4. Lea el mensaje de confirmacion.
5. Seleccione **Sí** para continuar o **No** para cancelar.
6. Espere el mensaje **Transferencia y publicacion completadas**.

Durante esta accion, la aplicacion copia hacia la base operativa los esquemas, versiones, rangos, grupos y celdas del Run seleccionado. No transfiere los datos temporales de extraccion.

Al finalizar:

- Las versiones del Run quedan publicadas.
- El esquema apunta a su version activa.
- Se conserva una auditoria de la publicacion.
- Las versiones anteriores no se eliminan.

La transferencia se puede reintentar cuando sea necesario. No obstante, si existe un error, no repita la accion hasta revisar el mensaje presentado y confirmar el estado del Run.

## 11. Estados habituales de un Run

| Estado | Significado | Accion recomendada |
| --- | --- | --- |
| `PENDING` | Run creado, aun no procesado. | Ejecutar la migracion cuando corresponda. |
| `RUNNING` | Migracion en curso. | Esperar la finalizacion o detenerla solo si es necesario. |
| `LOADED` | Carga terminada en staging. | Ejecutar la validacion. |
| `CANCELLED` | Run detenido o cancelado. | Usar el Run sucesor indicado por la aplicacion. |
| `TRANSFERRING` | Transferencia hacia la base operativa en curso. | Esperar el resultado; no cerrar ni repetir la operacion. |
| `PUBLISHED` | Run transferido y publicado. | Conservar el Run ID como evidencia de la ejecucion. |

## 12. Errores frecuentes

| Mensaje o situacion | Accion recomendada |
| --- | --- |
| Configuracion pendiente | Solicite a soporte que revise las variables de entorno o el archivo `.env`. |
| SQL Server no disponible | Verifique red, VPN y disponibilidad del servidor. Luego pruebe las conexiones. |
| Acceso a SQL Server denegado | Solicite la revision de credenciales y permisos sobre la base fuente. |
| MongoDB no disponible | Verifique red, VPN, URI y disponibilidad de MongoDB. |
| Acceso a MongoDB denegado | Solicite los permisos necesarios en la base de migracion u operativa. |
| Tiempo de espera agotado | Espere, revise la conectividad y vuelva a intentar. Si persiste, escale a soporte. |
| Datos de entrada invalidos | Revise el Run ID, el tipo de esquema, los productos y el tamano de lote. |
| Conflicto de datos o clave duplicada | No repita la operacion sobre el mismo borrador sin que soporte revise el Run y los datos. |
| Celdas ambiguas o conflicto global | No publique. Solicite la revision de las tasas fuente y de las reglas de consolidacion. |

Cuando aparezca una ventana de error:

1. Anote el **codigo de incidente** y el Run ID.
2. Revise la recomendacion mostrada en la ventana.
3. Si requiere soporte, use **Copiar detalles** y comparta la informacion con el equipo responsable.
4. No incluya credenciales en correos, tickets ni capturas de pantalla.

## 13. Evidencia y cierre operativo

Al finalizar una migracion publicada, conserve como minimo:

- Run ID publicado.
- Tipo de esquema migrado.
- Fecha y hora de la ejecucion.
- Resultado de la validacion.
- Conteos mostrados en el registro de actividad.
- Codigo de incidente, si se presento alguno.
- Aprobacion de publicacion, de acuerdo con el procedimiento del area.

La evidencia detallada de la carga, validaciones, excepciones y transferencia se conserva en la base de migracion para consulta de soporte y auditoria.

## 14. Recomendaciones de uso

- Use un Run ID nuevo para cada migracion nueva.
- Ejecute primero una migracion controlada sobre productos especificos cuando asi lo defina el plan de trabajo.
- No publique si existen rechazos que no hayan sido evaluados o controles de validacion pendientes.
- No cierre la aplicacion mientras una migracion o transferencia este en curso.
- Coordine con soporte antes de modificar la configuracion tecnica o repetir una publicacion con incidencias.
