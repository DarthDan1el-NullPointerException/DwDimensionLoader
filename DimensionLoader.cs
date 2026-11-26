
using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DwDimensionLoader;

/// <summary>
/// Define los datos y las consultas necesarias para cargar una dimensión.
/// </summary>
public record DimensionDefinition(
    string Name,
    Func<SqlConnection, Task<DataTable>> ExtractAsync,
    string StagingDdl,
    string MergeSql);

/// <summary>
/// Se encarga de cargar las dimensiones desde la base de datos de origen al DataWarehouse.
/// </summary>
public class DimensionLoader
{
    private readonly ILogger<DimensionLoader> _logger;
    private readonly string _origen;
    private readonly string _destino;

    public DimensionLoader(IConfiguration config, ILogger<DimensionLoader> logger)
    {
        _logger = logger;
        // Las conexiones se configuran en appsettings.json.
        _origen = config.GetConnectionString("Origen")
                  ?? throw new InvalidOperationException("Falta ConnectionStrings:Origen");
        _destino = config.GetConnectionString("Destino")
                   ?? throw new InvalidOperationException("Falta ConnectionStrings:Destino");
    }

    /// <summary>Carga las dimensiones una por una.</summary>
    public async Task RunAsync()
    {
        var total = Stopwatch.StartNew();
        _logger.LogInformation("Inicio de la carga de dimensiones.");

        foreach (var dim in BuildDefinitions())
        {
            try
            {
                await LoadDimensionAsync(dim);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cargando {Dimension}", dim.Name);
                throw;
            }
        }

        _logger.LogInformation("Carga de dimensiones terminada en {Ms} ms.", total.ElapsedMilliseconds);
    }

    /// <summary>Extrae y carga los datos de una dimensión.</summary>
    private async Task LoadDimensionAsync(DimensionDefinition dim)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("[{Dim}] Extrayendo datos del origen...", dim.Name);

        // Extraer datos
        DataTable data;
        await using (var src = new SqlConnection(_origen))
        {
            await src.OpenAsync();
            data = await dim.ExtractAsync(src);
        }
        _logger.LogInformation("[{Dim}] {Rows} filas extraídas.", dim.Name, data.Rows.Count);

        // Cargar los datos en la tabla temporal y actualizar la dimensión.
        await using var dest = new SqlConnection(_destino);
        await dest.OpenAsync();
        await using var tx = (SqlTransaction)await dest.BeginTransactionAsync();

        await using (var create = new SqlCommand(dim.StagingDdl, dest, tx))
            await create.ExecuteNonQueryAsync();

        // Carga masiva de los datos.
        using (var bulk = new SqlBulkCopy(dest, SqlBulkCopyOptions.Default, tx))
        {
            bulk.DestinationTableName = "#stg";
            foreach (DataColumn col in data.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(data);
        }

        await using (var merge = new SqlCommand(dim.MergeSql, dest, tx))
        {
            int affected = await merge.ExecuteNonQueryAsync();
            _logger.LogInformation("[{Dim}] {Affected} filas insertadas/actualizadas.", dim.Name, affected);
        }

        await tx.CommitAsync();
        _logger.LogInformation("[{Dim}] Carga completada en {Ms} ms.", dim.Name, sw.ElapsedMilliseconds);
    }

    // Consultas y configuración de las dimensiones.
    private static IEnumerable<DimensionDefinition> BuildDefinitions()
    {
        // DimCliente
        yield return new DimensionDefinition(
            "DimCliente",
            src => QueryAsync(src, @"
                SELECT id_cliente AS ClienteIdOrigen, nombre AS Nombre, apellido AS Apellido,
                       email AS Email, pais AS Pais, fecha_registro AS FechaRegistro
                FROM dbo.cliente"),
            @"CREATE TABLE #stg (ClienteIdOrigen INT, Nombre NVARCHAR(MAX), Apellido NVARCHAR(MAX),
                                 Email NVARCHAR(MAX), Pais NVARCHAR(MAX), FechaRegistro DATE);",
            @"MERGE dbo.DimCliente AS t
              USING #stg AS s ON t.ClienteIdOrigen = s.ClienteIdOrigen
              WHEN MATCHED THEN UPDATE SET
                   t.Nombre = s.Nombre, t.Apellido = s.Apellido, t.Email = s.Email,
                   t.Pais = s.Pais, t.FechaRegistro = s.FechaRegistro
              WHEN NOT MATCHED THEN
                   INSERT (ClienteIdOrigen, Nombre, Apellido, Email, Pais, FechaRegistro)
                   VALUES (s.ClienteIdOrigen, s.Nombre, s.Apellido, s.Email, s.Pais, s.FechaRegistro);");

        // DimProducto
        yield return new DimensionDefinition(
            "DimProducto",
            src => QueryAsync(src, @"
                SELECT p.id_producto AS ProductoIdOrigen, p.nombre AS Nombre, p.marca AS Marca,
                       c.nombre AS Categoria, p.precio AS Precio, p.fecha_alta AS FechaAlta
                FROM dbo.producto p
                LEFT JOIN dbo.categoria c ON c.id_categoria = p.id_categoria"),
            @"CREATE TABLE #stg (ProductoIdOrigen INT, Nombre NVARCHAR(MAX), Marca NVARCHAR(MAX),
                                 Categoria NVARCHAR(MAX), Precio DECIMAL(18,2), FechaAlta DATE);",
            @"MERGE dbo.DimProducto AS t
              USING #stg AS s ON t.ProductoIdOrigen = s.ProductoIdOrigen
              WHEN MATCHED THEN UPDATE SET
                   t.Nombre = s.Nombre, t.Marca = s.Marca, t.Categoria = s.Categoria,
                   t.Precio = s.Precio, t.FechaAlta = s.FechaAlta
              WHEN NOT MATCHED THEN
                   INSERT (ProductoIdOrigen, Nombre, Marca, Categoria, Precio, FechaAlta)
                   VALUES (s.ProductoIdOrigen, s.Nombre, s.Marca, s.Categoria, s.Precio, s.FechaAlta);");

        // DimFuente
        yield return new DimensionDefinition(
            "DimFuente",
            src => QueryAsync(src, @"
                SELECT id_fuente AS FuenteIdOrigen, nombre AS Nombre, tipo AS Tipo, url_base AS UrlBase
                FROM dbo.fuente_datos"),
            @"CREATE TABLE #stg (FuenteIdOrigen INT, Nombre NVARCHAR(MAX), Tipo NVARCHAR(MAX),
                                 UrlBase NVARCHAR(MAX));",
            @"MERGE dbo.DimFuente AS t
              USING #stg AS s ON t.FuenteIdOrigen = s.FuenteIdOrigen
              WHEN MATCHED THEN UPDATE SET
                   t.Nombre = s.Nombre, t.Tipo = s.Tipo, t.UrlBase = s.UrlBase
              WHEN NOT MATCHED THEN
                   INSERT (FuenteIdOrigen, Nombre, Tipo, UrlBase)
                   VALUES (s.FuenteIdOrigen, s.Nombre, s.Tipo, s.UrlBase);");

        // DimFecha
        yield return new DimensionDefinition(
            "DimFecha",
            GenerateDatesAsync,
            @"CREATE TABLE #stg (FechaKey INT, Fecha DATE, Anio INT, Mes INT,
                                 NombreMes NVARCHAR(20), Dia INT, Trimestre INT);",
            @"MERGE dbo.DimFecha AS t
              USING #stg AS s ON t.FechaKey = s.FechaKey
              WHEN NOT MATCHED THEN
                   INSERT (FechaKey, Fecha, Anio, Mes, NombreMes, Dia, Trimestre)
                   VALUES (s.FechaKey, s.Fecha, s.Anio, s.Mes, s.NombreMes, s.Dia, s.Trimestre);");
    }

    // Métodos auxiliares.

    /// <summary>Ejecuta una consulta y guarda sus resultados en un DataTable.</summary>
    private static async Task<DataTable> QueryAsync(SqlConnection conn, string sql)
    {
        var table = new DataTable();
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        table.Load(reader);
        return table;
    }

    /// <summary>Genera las fechas según los años registrados en las opiniones.</summary>
    private static async Task<DataTable> GenerateDatesAsync(SqlConnection src)
    {
        var start = new DateTime(DateTime.Today.Year, 1, 1);
        var end = new DateTime(DateTime.Today.Year, 12, 31);

        await using (var cmd = new SqlCommand(
            "SELECT MIN(CAST(fecha_opinion AS date)), MAX(CAST(fecha_opinion AS date)) FROM dbo.opinion", src))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (await r.ReadAsync() && !r.IsDBNull(0))
            {
                start = new DateTime(r.GetDateTime(0).Year, 1, 1);
                end = new DateTime(r.GetDateTime(1).Year, 12, 31);
            }
        }

        var table = new DataTable();
        table.Columns.Add("FechaKey", typeof(int));
        table.Columns.Add("Fecha", typeof(DateTime));
        table.Columns.Add("Anio", typeof(int));
        table.Columns.Add("Mes", typeof(int));
        table.Columns.Add("NombreMes", typeof(string));
        table.Columns.Add("Dia", typeof(int));
        table.Columns.Add("Trimestre", typeof(int));

        var es = new CultureInfo("es-ES");
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            table.Rows.Add(
                d.Year * 10000 + d.Month * 100 + d.Day,   // Formato yyyymmdd
                d,
                d.Year,
                d.Month,
                es.TextInfo.ToTitleCase(d.ToString("MMMM", es)),
                d.Day,
                (d.Month - 1) / 3 + 1);
        }
        return table;
    }
}
