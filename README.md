# Configured Pricing Migration

Aplicacion WinForms independiente para ejecutar manualmente la carga inicial de `dbo.SI_FinTasa`. El staging, los controles y el resultado preliminar se almacenan en una base MongoDB de migracion separada. Solo la accion explicita de transferencia copia un `Run ID` validado y lo publica en la base operativa.

Las conexiones y bases de datos se configuran exclusivamente mediante variables
de entorno o el archivo `.env`; no se muestran ni se editan desde la interfaz.
La interfaz permite seleccionar el tipo de esquema, el Run ID, el filtro de
productos y el tamaño de lote.

De forma opcional, la aplicacion busca un archivo `.env` desde su directorio de ejecucion hacia los directorios padre y usa el primero que encuentre. Normalmente debe ubicarse en `apps/ConfiguredPricingMigration/.env`. Las variables de entorno reales tienen prioridad sobre los valores del archivo. El archivo `.env` esta excluido del control de versiones.
Se admiten comillas, lineas `export NOMBRE=valor` y comentarios iniciados con `#` fuera de comillas.
El archivo `apps/ConfiguredPricingMigration/.env.example` contiene una plantilla sin credenciales.

```dotenv
SQL_CONNECTION_STRING="Server=...;Database=...;Trusted_Connection=True;"
MIGRATION_MONGO_CONNECTION_STRING="mongodb://..."
MIGRATION_MONGO_DATABASE="ConfiguredPricingMigration"
TARGET_MONGO_CONNECTION_STRING="mongodb://..."
TARGET_MONGO_DATABASE="ConfiguredPricing"
MIGRATION_BATCH_SIZE=10000
MIGRATION_PRODUCT_IDS=1028,1035,1041
```

| Configuracion | Variable de entorno | Uso |
| --- | --- | --- |
| SQL Server | `SQL_CONNECTION_STRING` | Cadena de conexion de solo lectura a SQL Server, desde donde se consulta `dbo.SI_FinTasa`, `dbo.SI_FinMonto` y `dbo.SI_FinPlazo`. |
| MongoDB migracion | `MIGRATION_MONGO_CONNECTION_STRING` | Cadena de conexion a la base no operativa donde se almacenan staging, checkpoints, conjuntos preliminares, validaciones y excepciones. |
| Base migracion | `MIGRATION_MONGO_DATABASE` | Nombre de la base MongoDB/DocumentDB de staging. El valor predeterminado es `ConfiguredPricingMigration`. |
| MongoDB operativo | `TARGET_MONGO_CONNECTION_STRING` | Cadena de conexion a la base operativa que utiliza Discount Pricing S. Solo se usa al transferir y publicar un Run validado. |
| Base operativa | `TARGET_MONGO_DATABASE` | Nombre de la base MongoDB/DocumentDB operativa. El valor predeterminado es `ConfiguredPricing`. |
| Run ID | No aplica | Identificador de una ejecución de un solo tipo de esquema. Ordinario conserva checkpoints por producto; Línea Base y Especial conservan un checkpoint global por `idTasa`. |
| idProductos separados por coma | `MIGRATION_PRODUCT_IDS` | Filtro disponible solo para Ordinario. Línea Base y Especial leen los registros elegibles de `SI_FinTasa` sin filtrar ni exigir `idProducto`. |
| Tamano de lote | `MIGRATION_BATCH_SIZE` | Filas leidas y confirmadas por iteracion. Acepta valores entre 1,000 y 100,000; el predeterminado es 10,000. |

La vista previa de matrices está temporalmente oculta en la interfaz. La carga,
validación y publicación de Runs continúan disponibles desde la pantalla de
migración.

Para Ordinario, si `idProductos` esta vacio, se resuelven productos vigentes del modulo 1 con tasas de credito elegibles antes de iniciar la migracion. Los IDs ingresados manualmente se validan con los mismos criterios. Línea Base y Especial no consultan `SI_FinProducto` para descubrir filas: recorren `SI_FinTasa` por `idTasa`, con tipos de tasa vigentes del módulo 1 y, en Especial, monto y establecimiento vigentes. `idProducto` solo se conserva como trazabilidad cuando está informado.

Durante la carga, el boton `Ejecutar migración` sincroniza primero los catalogos vigentes del modulo de creditos hacia la base operativa y registra el snapshot en el Run ID. Si falla esa sincronizacion, el ETL no inicia. `Detener migración` en Ordinario conserva los productos terminados y crea un Run para los pendientes. Línea Base y Especial deben publicarse como una unidad global; al detenerlos se cierra el Run sin publicar y se crea un sucesor para reextraer el esquema completo.

## Transferencia y publicacion

`Transferir y publicar Run validado` no usa un endpoint HTTP. Copia desde la base de migracion solo los esquemas, versiones, rangos, grupos genericos y celdas del `Run ID` seleccionado hacia la base operativa. Antes de transferir exige una validacion correcta y ausencia de celdas ambiguas. La transferencia se puede reintentar: los documentos destino se reemplazan por su misma clave y no se copian colecciones de staging.

`ConfiguredPricingSchema.typeSchema` identifica la forma del esquema. La ventana ofrece tres tarjetas: **Ordinario**, **Línea Base** y **Especial**; cada Run migra solo el tipo seleccionado.

| typeSchema | Alcance e identidad | Seleccion de registros y dimensiones | pricingMode |
| --- | --- | --- | --- |
| `ORDINARY` (Ordinario) | Un esquema por producto. | Tarifario completo, excepto los tipos 35, 60, 62 y 64 que pertenecen a esquemas especiales. | `STANDARD` y `NEGOTIABLE`, según `lNegociable`. |
| `BASELINE` (Línea Base) | Un único esquema global, sin `productId`. | Tipos de tasa 35, 60 y 62. Cada celda se distingue por `rateTypeId`; no tiene otras dimensiones tarifarias. | `STANDARD` y `NEGOTIABLE`, según `lNegociable`. |
| `SPECIAL` (Especial) | Un único esquema global, sin `productId`. | Tipo de tasa 64; las celdas se distinguen por monto y establecimiento. | Solo `NEGOTIABLE`. |

Los IDs de BASELINE y SPECIAL no incluyen producto. Para consolidar los registros de todos los productos, se omite el producto de la clave de las celdas; sus IDs de origen se conservan como trazabilidad, pero no forman parte de la clave ni del filtro. Si varios productos producen la misma celda con valores iguales, se consolida y se unen los IDs legacy; si los valores difieren, la validacion registra `SCHEMA_SCOPE_CONFLICT` y bloquea la publicacion.

Las versiones se transfieren primero como `DRAFT`; al finalizar la copia se marcan `PUBLISHED` y se actualiza el `activeVersionId` de cada esquema. Las versiones anteriores no se eliminan. La base operativa recibe una auditoria `INITIAL_MIGRATION_PUBLISHED`; el estado y la evidencia de la transferencia se mantienen en `ConfiguredPricingMigrationRuns` de la base de migracion.

## Celdas ambiguas

Una celda solo puede tener una tasa por `idTipoTasa` para la misma clave de esquema. Ordinario mantiene las coordenadas completas; Línea Base usa `rateTypeId` como coordenada; Especial contiene `amountRangeId` y el grupo de establecimiento. Grupos, montos y plazos se reutilizan globalmente mediante IDs deterministas. Todo catálogo SQL que posea `idModulo` se restringe a registros vigentes del módulo 1 (créditos); los demás catálogos se restringen a registros vigentes. La extracción y la validación rechazan duplicados o conflictos de valores después de proyectar las dimensiones del esquema elegido.

La migracion inicial crea grupos `AUTO-*` con IDs canonicos derivados de sus miembros. `DEFAULT` es solo una etiqueta informativa cuando el grupo contiene todos los miembros vigentes de una dimension; no altera su identidad. La consolidacion conserva la trazabilidad de los IDs legacy.

## Compilacion y distribucion

Para compilar la aplicacion durante el desarrollo:

```powershell
dotnet build .\ConfiguredPricingMigration.UI\ConfiguredPricingMigration.UI.csproj -c Release
```

Para generar un ejecutable autocontenido para Windows de 64 bits, sin requerir
que el equipo destino tenga instalado .NET:

```powershell
dotnet publish .\ConfiguredPricingMigration.UI\ConfiguredPricingMigration.UI.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o .\publish
```

El artefacto se genera como `publish\ConfiguredPricingMigration.UI.exe`.

## Uso del ejecutable

1. Copie la carpeta `publish` completa al equipo donde se ejecutara la migracion.
2. Cree `publish\.env` a partir de `.env.example` y reemplace los valores de ejemplo por las cadenas de conexion del ambiente correspondiente.
3. Restrinja los permisos NTFS de `publish\.env` a la cuenta o grupo autorizado para ejecutar la herramienta. No distribuya este archivo mediante control de versiones ni lo incluya en paquetes compartidos.
4. Ejecute `ConfiguredPricingMigration.UI.exe` desde la carpeta `publish`.
5. Use `Probar configuracion` antes de iniciar una migracion y valide el Run antes de transferirlo y publicarlo.

El archivo `.env` se busca primero junto al ejecutable y sus valores solo se
aplican cuando no existe una variable de entorno del mismo nombre. Para un
despliegue administrado, configure las variables de entorno de Windows para la
cuenta operativa y omita el archivo `.env`.
