# DwDimensionLoader

Proceso ETL en .NET 8 que carga las dimensiones del DataWarehouse
(DimCliente, DimProducto, DimFuente, DimFecha) desde la base de datos
origen `opiniones_clientes` hacia `DataWarehouse` en SQL Server.

## Como ejecutar
1. Crear la base `DataWarehouse` con el script SQL del modelo estrella.
2. Ajustar las cadenas de conexión en `appsettings.json`.
3. Ejecutar: `dotnet run`

## Caracteristicas
- Extracción con consultas SQL y carga masiva con SqlBulkCopy.
- Carga idempotente con MERGE (no duplica registros al re-ejecutar).
- Transacción por dimensión y logs con ILogger.
