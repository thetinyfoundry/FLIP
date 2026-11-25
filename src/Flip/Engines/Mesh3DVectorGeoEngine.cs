using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

/// <summary>
/// Motore built-in per 3D Mesh (STL, OBJ, PLY, GLB, GLTF, 3MF, DAE, FBX),
/// Blueprint Tecnici 3D (3D -> PNG/JPG/WEBP/BMP/PDF), CAD/Vector (SVG ↔ DXF → immagine/PDF)
/// e Geospatial (GPX, KML, GeoJSON).
/// </summary>
public sealed class Mesh3DVectorGeoEngine : IConversionEngine
{
    public string Id => "geometry_builtin";
    public string DisplayName => "3D Mesh, CAD Vector & Geospatial Engine";
    public bool IsExternal => false;

    private readonly List<ConversionEdge> _supportedEdges = [];

    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    // ── Formati supportati ────────────────────────────────────────────────
    private static readonly string[] MeshFormats = ["stl", "obj", "ply", "glb", "gltf", "3mf", "dae", "fbx"];
    private static readonly string[] VectorFormats = ["svg", "dxf"];
    private static readonly string[] RasterOutputs = ["png", "jpg", "jpeg", "webp", "bmp"];
    private static readonly string[] GeoFormats = ["gpx", "kml", "geojson"];

    public Mesh3DVectorGeoEngine()
    {
        // 3D Mesh ↔ Mesh (tutti gli 8 formati 3D)
        foreach (var src in MeshFormats)
            foreach (var dst in MeshFormats)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));

        // 3D Mesh → Raster Blueprint (4 viste + cartiglio)
        foreach (var src in MeshFormats)
            foreach (var dst in RasterOutputs)
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 12));

        // 3D Mesh → GIF Turntable 360° (1 salto diretto)
        foreach (var src in MeshFormats)
            _supportedEdges.Add(new ConversionEdge(src, "gif", Id, 12));

        // 3D Mesh → PDF Blueprint
        foreach (var src in MeshFormats)
            _supportedEdges.Add(new ConversionEdge(src, "pdf", Id, 12));

        // Mesh → SVG / DXF (proiezione 2D)
        foreach (var src in MeshFormats)
            foreach (var dst in VectorFormats)
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 14));

        // Conversione bidirezionale DXF ↔ SVG
        _supportedEdges.Add(new ConversionEdge("dxf", "svg", Id, 10));
        _supportedEdges.Add(new ConversionEdge("svg", "dxf", Id, 10));

        // SVG/DXF → Raster
        foreach (var src in VectorFormats)
            foreach (var dst in RasterOutputs)
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));

        // SVG/DXF → PDF
        foreach (var src in VectorFormats)
            _supportedEdges.Add(new ConversionEdge(src, "pdf", Id, 10));

        // Geospatial ↔ Geospatial
        foreach (var src in GeoFormats)
            foreach (var dst in GeoFormats)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));
    }

    public bool IsAvailable(FlipSettings settings) => true;

    public Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;
            if (string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(dstPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Il percorso di output coincide con il file sorgente.");

            if (!File.Exists(srcPath))
                return Task.FromResult(new EngineExecutionResult(false, "", $"File non trovato: {srcPath}"));

            string srcExt = (string.IsNullOrEmpty(context.SourceExtension) ? Path.GetExtension(srcPath) : context.SourceExtension).TrimStart('.').ToLowerInvariant();
            string dstExt = (string.IsNullOrEmpty(context.TargetExtension) ? Path.GetExtension(dstPath) : context.TargetExtension).TrimStart('.').ToLowerInvariant();
            if (!SupportedEdges.Any(edge => edge.SourceExtension == srcExt && edge.TargetExtension == dstExt))
                return Task.FromResult(new EngineExecutionResult(false, "", $"Conversione non supportata: {srcExt} -> {dstExt}"));

            string? dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            context.Progress?.Report(0.10);

            // Mesh ↔ Mesh
            if (MeshFormats.Contains(srcExt) && MeshFormats.Contains(dstExt))
                ConvertMesh(srcPath, srcExt, dstPath, dstExt);
            // Mesh → Raster Blueprint (4 viste + cartiglio)
            else if (MeshFormats.Contains(srcExt) && RasterOutputs.Contains(dstExt))
                RenderMeshBlueprintToImage(srcPath, srcExt, dstPath, dstExt, context);
            // Mesh → GIF Turntable 360°
            else if (MeshFormats.Contains(srcExt) && dstExt == "gif")
                RenderTurntableGifToFile(srcPath, srcExt, dstPath, context.CancellationToken, context.Progress);
            // Mesh → PDF Blueprint
            else if (MeshFormats.Contains(srcExt) && dstExt == "pdf")
                RenderMeshBlueprintToPdf(srcPath, srcExt, dstPath, context);
            // Mesh → Vector (proiezione 2D)
            else if (MeshFormats.Contains(srcExt) && VectorFormats.Contains(dstExt))
                MeshToVector(srcPath, srcExt, dstPath, dstExt);
            else if (srcExt == "dxf" && dstExt == "svg")
                LoadDxfProjection(srcPath).Save(dstPath);
            else if (srcExt == "svg" && dstExt == "dxf")
                ConvertSvgToDxf(srcPath, dstPath);
            // SVG/DXF → Raster
            else if (VectorFormats.Contains(srcExt) && RasterOutputs.Contains(dstExt))
                RenderVectorToImage(srcPath, srcExt, dstPath, dstExt, context);
            // SVG/DXF → PDF
            else if (VectorFormats.Contains(srcExt) && dstExt == "pdf")
                RenderVectorToPdf(srcPath, srcExt, dstPath, context);
            // Geospatial ↔ Geospatial
            else if (GeoFormats.Contains(srcExt) && GeoFormats.Contains(dstExt))
                ConvertGeo(srcPath, srcExt, dstPath, dstExt);
            else
                return Task.FromResult(new EngineExecutionResult(false, "", $"Conversione non supportata: {srcExt} -> {dstExt}"));

            context.Progress?.Report(1.0);
            long bytes = File.Exists(dstPath) ? new FileInfo(dstPath).Length : 0;
            return Task.FromResult(new EngineExecutionResult(true, dstPath, null, bytes));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", "Conversione geometry annullata."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", $"Errore geometry ({context.SourceExtension} -> {context.TargetExtension}): {ex.Message}"));
        }
    }

    /// <summary>Ispeziona un file 3D restituendo il numero di vertici unici e di triangoli.</summary>
    internal static (int VertexCount, int TriangleCount) InspectMeshCounts(string path, string? ext = null)
    {
        string normalized = (string.IsNullOrEmpty(ext) ? Path.GetExtension(path) : ext).TrimStart('.').ToLowerInvariant();
        var tris = LoadMesh(path, normalized);
        var (verts, faces) = BuildIndexedMesh(tris);
        return (verts.Count, faces.Count);
    }

    /// <summary>Statistiche tecniche di un modello 3D per l'header di FLIP QuickLook.</summary>
    internal readonly record struct MeshStats(int VertexCount, int TriangleCount, float SizeX, float SizeY, float SizeZ);

    /// <summary>Ispeziona un file 3D restituendo vertici unici, triangoli e dimensioni del bounding box.</summary>
    internal static MeshStats InspectMeshStats(string path, string? ext = null)
    {
        string normalized = (string.IsNullOrEmpty(ext) ? Path.GetExtension(path) : ext).TrimStart('.').ToLowerInvariant();
        var tris = LoadMesh(path, normalized);
        var (verts, faces) = BuildIndexedMesh(tris);
        if (verts.Count == 0) return new(0, faces.Count, 0, 0, 0);
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var v in verts)
        {
            minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y); minZ = Math.Min(minZ, v.Z);
            maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y); maxZ = Math.Max(maxZ, v.Z);
        }
        return new(verts.Count, faces.Count, maxX - minX, maxY - minY, maxZ - minZ);
    }

    /// <summary>Genera il Blueprint 4 viste (1600x1200) come PNG, usato dall'anteprima QuickLook.</summary>
    internal static void RenderBlueprintPng(string srcPath, string? ext, string dstPath)
    {
        string normalized = (string.IsNullOrEmpty(ext) ? Path.GetExtension(srcPath) : ext).TrimStart('.').ToLowerInvariant();
        var tris = LoadMesh(srcPath, normalized);
        if (tris.Count == 0) throw new IOException("Il modello 3D non contiene triangoli da renderizzare.");
        RenderBlueprintPngFromTriangles(Path.GetFileName(srcPath), tris, dstPath);
    }

    private static void RenderBlueprintPngFromTriangles(string fileName, List<Triangle> tris, string dstPath)
    {
        const int width = 1600, height = 1200;
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            DrawMeshBlueprintCanvas(canvas, width, height, fileName, tris);
            canvas.Flush();
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("Encoder PNG non disponibile.");
        using var fs = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(fs);
    }

    /// <summary>
    /// Carica una sola volta la mesh 3D e produce insieme dati interattivi, statistiche e Blueprint 4 viste
    /// per aprire FLIP QuickLook istantaneamente.
    /// </summary>
    internal static (InteractiveMeshData? Mesh, MeshStats Stats) PrepareQuickLookPreviewData(
        string srcPath, string? ext, string blueprintDstPath, CancellationToken token = default)
    {
        string normalized = (string.IsNullOrEmpty(ext) ? Path.GetExtension(srcPath) : ext).TrimStart('.').ToLowerInvariant();
        var tris = LoadMesh(srcPath, normalized);
        if (tris.Count == 0) throw new IOException("Il modello 3D non contiene triangoli da visualizzare.");
        token.ThrowIfCancellationRequested();

        var (verts, faces) = BuildIndexedMesh(tris);
        var mesh = BuildInteractiveMeshDataFromIndexed(verts, faces, normalized);
        var stats = new MeshStats(mesh.VertexCount, mesh.TriangleCount, mesh.SizeX, mesh.SizeY, mesh.SizeZ);
        token.ThrowIfCancellationRequested();

        RenderBlueprintPngFromTriangles(Path.GetFileName(srcPath), tris, blueprintDstPath);
        return (mesh, stats);
    }

    /// <summary>Genera una GIF Turntable 360° direttamente su file per un modello 3D.</summary>
    internal static void RenderTurntableGifToFile(
        string srcPath,
        string? ext,
        string dstPath,
        CancellationToken ct = default,
        IProgress<double>? progress = null)
    {
        int frames = Turntable3DQuickTool.DefaultFrames;
        int size = Turntable3DQuickTool.DefaultSize;
        int fps = Turntable3DQuickTool.DefaultFps;
        var rendered = new int[frames][];
        RenderTurntableFrames(
            srcPath,
            string.IsNullOrEmpty(ext) ? Path.GetExtension(srcPath) : ext,
            frames,
            size,
            (frame, pixels) =>
            {
                rendered[frame] = pixels;
                progress?.Report(0.10 + 0.80 * (frame + 1) / frames);
            },
            ct);
        AnimatedGifWriter.Write(dstPath, size, size, rendered, fps, ct);
    }

    /// <summary>
    /// Mesh indicizzata pronta per il viewport interattivo: centrata nell'origine, lato massimo 2.0 unità,
    /// Y verso l'alto e normali mediate per vertice (smooth shading).
    /// </summary>
    internal sealed class InteractiveMeshData
    {
        /// <summary>Posizioni XYZ interlacciate (3 float per vertice).</summary>
        public required float[] Positions { get; init; }
        /// <summary>Normali XYZ normalizzate e interlacciate (3 float per vertice).</summary>
        public required float[] Normals { get; init; }
        /// <summary>Indici dei triangoli (3 per faccia).</summary>
        public required int[] Indices { get; init; }
        /// <summary>Dimensioni originali del bounding box, nelle unità del file.</summary>
        public float SizeX { get; init; }
        public float SizeY { get; init; }
        public float SizeZ { get; init; }
        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>
    /// Carica una mesh (OBJ, STL, FBX, GLB, GLTF, PLY, 3MF, DAE), la normalizza per il viewport
    /// (centro in (0,0,0), lato massimo 2.0, Z-up → Y-up per STL/3MF) e calcola normali smooth per vertice
    /// sommando le normali dei triangoli adiacenti pesate per area.
    /// </summary>
    internal static InteractiveMeshData BuildInteractiveMeshData(string path, string? ext = null)
    {
        string normalized = (string.IsNullOrEmpty(ext) ? Path.GetExtension(path) : ext).TrimStart('.').ToLowerInvariant();
        var tris = LoadMesh(path, normalized);
        if (tris.Count == 0) throw new IOException("Il modello 3D non contiene triangoli da visualizzare.");
        var (verts, faces) = BuildIndexedMesh(tris);
        return BuildInteractiveMeshDataFromIndexed(verts, faces, normalized);
    }

    private static InteractiveMeshData BuildInteractiveMeshDataFromIndexed(
        List<Vec3> verts, List<(int A, int B, int C)> faces, string normalized)
    {
        if (verts.Count == 0 || faces.Count == 0) throw new IOException("Il modello 3D non contiene geometria valida.");

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var v in verts)
        {
            minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y); minZ = Math.Min(minZ, v.Z);
            maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y); maxZ = Math.Max(maxZ, v.Z);
        }
        float sizeX = maxX - minX, sizeY = maxY - minY, sizeZ = maxZ - minZ;
        float maxSide = Math.Max(sizeX, Math.Max(sizeY, sizeZ));
        float scale = maxSide > 1e-12f && float.IsFinite(maxSide) ? 2f / maxSide : 1f;
        float cx = (minX + maxX) * 0.5f, cy = (minY + maxY) * 0.5f, cz = (minZ + maxZ) * 0.5f;
        bool zUp = normalized is "stl" or "3mf";

        var positions = new float[verts.Count * 3];
        for (int i = 0; i < verts.Count; i++)
        {
            float x = (verts[i].X - cx) * scale;
            float y = (verts[i].Y - cy) * scale;
            float z = (verts[i].Z - cz) * scale;
            int o = i * 3;
            positions[o] = x;
            if (zUp)
            {
                // Z-up (stampa 3D/CAD) → Y-up del viewport, mantenendo la terna destrorsa.
                positions[o + 1] = z;
                positions[o + 2] = -y;
            }
            else
            {
                positions[o + 1] = y;
                positions[o + 2] = z;
            }
        }

        var indices = new int[faces.Count * 3];
        var normals = new float[positions.Length];
        for (int f = 0; f < faces.Count; f++)
        {
            var (a, b, c) = faces[f];
            indices[f * 3] = a; indices[f * 3 + 1] = b; indices[f * 3 + 2] = c;
            int ia = a * 3, ib = b * 3, ic = c * 3;
            float ux = positions[ib] - positions[ia], uy = positions[ib + 1] - positions[ia + 1], uz = positions[ib + 2] - positions[ia + 2];
            float vx = positions[ic] - positions[ia], vy = positions[ic + 1] - positions[ia + 1], vz = positions[ic + 2] - positions[ia + 2];
            // Prodotto vettoriale non normalizzato: il modulo è 2× l'area, quindi la somma è pesata per area.
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            if (!float.IsFinite(nx) || !float.IsFinite(ny) || !float.IsFinite(nz)) continue;
            normals[ia] += nx; normals[ia + 1] += ny; normals[ia + 2] += nz;
            normals[ib] += nx; normals[ib + 1] += ny; normals[ib + 2] += nz;
            normals[ic] += nx; normals[ic + 1] += ny; normals[ic + 2] += nz;
        }
        for (int o = 0; o < normals.Length; o += 3)
        {
            float len = MathF.Sqrt(normals[o] * normals[o] + normals[o + 1] * normals[o + 1] + normals[o + 2] * normals[o + 2]);
            if (len > 1e-20f && float.IsFinite(len))
            {
                normals[o] /= len; normals[o + 1] /= len; normals[o + 2] /= len;
            }
            else
            {
                normals[o] = 0; normals[o + 1] = 1; normals[o + 2] = 0;
            }
        }

        return new InteractiveMeshData
        {
            Positions = positions,
            Normals = normals,
            Indices = indices,
            SizeX = sizeX,
            SizeY = sizeY,
            SizeZ = sizeZ
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MODELLO MESH INTERMEDIO
    // ══════════════════════════════════════════════════════════════════════════

    private readonly struct Vec3(float x, float y, float z)
    {
        public readonly float X = x;
        public readonly float Y = y;
        public readonly float Z = z;
    }

    private readonly struct Triangle(Vec3 a, Vec3 b, Vec3 c)
    {
        public readonly Vec3 A = a;
        public readonly Vec3 B = b;
        public readonly Vec3 C = c;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MESH PARSERS
    // ══════════════════════════════════════════════════════════════════════════

    private static List<Triangle> LoadMesh(string path, string ext)
    {
        return ext switch
        {
            "stl" => LoadStl(path),
            "obj" => LoadObj(path),
            "ply" => LoadPly(path),
            "glb" or "gltf" => LoadGlbOrGltf(path, ext),
            "3mf" => Load3mf(path),
            "dae" => LoadDae(path),
            "fbx" => LoadFbx(path),
            _ => throw new IOException($"Formato mesh non supportato: {ext}")
        };
    }

    private static (List<Vec3> UniqueVerts, List<(int A, int B, int C)> Faces) BuildIndexedMesh(List<Triangle> tris)
    {
        var vertexMap = new Dictionary<(int, int, int), int>();
        var uniqueVerts = new List<Vec3>();
        var faces = new List<(int A, int B, int C)>(tris.Count);

        int GetOrAddVertex(Vec3 v)
        {
            var key = (
                (int)MathF.Round(v.X * 10000f),
                (int)MathF.Round(v.Y * 10000f),
                (int)MathF.Round(v.Z * 10000f)
            );
            if (vertexMap.TryGetValue(key, out int idx)) return idx;
            idx = uniqueVerts.Count;
            uniqueVerts.Add(v);
            vertexMap[key] = idx;
            return idx;
        }

        foreach (var t in tris)
        {
            int a = GetOrAddVertex(t.A);
            int b = GetOrAddVertex(t.B);
            int c = GetOrAddVertex(t.C);
            faces.Add((a, b, c));
        }

        return (uniqueVerts, faces);
    }

    // ── STL ────────────────────────────────────────────────────────────────

    private static List<Triangle> LoadStl(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length >= 84)
        {
            using var binary = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            stream.Position = 80;
            uint count = binary.ReadUInt32();
            if (84L + count * 50L == stream.Length)
                return LoadStlBinary(binary, count);
        }

        stream.Position = 0;
        using var text = new StreamReader(stream, new UTF8Encoding(false, true));
        return LoadStlAscii(text);
    }

    private static List<Triangle> LoadStlAscii(TextReader reader)
    {
        var tris = new List<Triangle>();
        static bool Is(string actual, string expected) => actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        string[] ReadLine()
        {
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            }
            return [];
        }

        void Expect(params string[] keywords)
        {
            if (!ReadLine().SequenceEqual(keywords, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"STL ASCII non valido: atteso '{string.Join(" ", keywords)}'.");
        }

        Vec3 Vertex()
        {
            string[] parts = ReadLine();
            if (parts.Length != 4 || !Is(parts[0], "vertex"))
                throw new IOException("STL ASCII non valido: atteso 'vertex x y z'.");
            return new(ParseNumber(parts[1]), ParseNumber(parts[2]), ParseNumber(parts[3]));
        }

        for (string[] opening = ReadLine(); opening.Length > 0; opening = ReadLine())
        {
            if (!Is(opening[0], "solid"))
                throw new IOException("STL non valido: lunghezza binaria incompatibile e intestazione ASCII assente.");
            while (true)
            {
                string[] facet = ReadLine();
                if (facet.Length > 0 && Is(facet[0], "endsolid")) break;
                if (facet.Length != 5 || !Is(facet[0], "facet") || !Is(facet[1], "normal"))
                    throw new IOException("STL ASCII non valido: atteso 'facet normal' o 'endsolid'.");
                for (int i = 2; i < facet.Length; i++) ParseNumber(facet[i]);
                Expect("outer", "loop");
                tris.Add(new(Vertex(), Vertex(), Vertex()));
                Expect("endloop");
                Expect("endfacet");
                if (tris.Count > 5_000_000) throw new IOException("STL: troppi triangoli.");
            }
        }

        if (tris.Count == 0) throw new IOException("STL ASCII: nessun triangolo valido trovato.");
        return tris;
    }

    private static List<Triangle> LoadStlBinary(BinaryReader reader, uint count)
    {
        if (count == 0 || count > 5_000_000)
            throw new IOException($"STL binario: conteggio triangoli non valido ({count}).");

        var tris = new List<Triangle>((int)count);
        Vec3 Vertex()
        {
            float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                throw new IOException("STL binario: coordinata non finita.");
            return new(x, y, z);
        }

        for (uint i = 0; i < count; i++)
        {
            reader.BaseStream.Seek(12, SeekOrigin.Current);
            tris.Add(new(Vertex(), Vertex(), Vertex()));
            reader.ReadUInt16();
        }

        return tris;
    }

    // ── OBJ ────────────────────────────────────────────────────────────────

    private static List<Triangle> LoadObj(string path)
    {
        var vertices = new List<Vec3>();
        var tris = new List<Triangle>();

        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                string[] p = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 &&
                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    vertices.Add(new Vec3(x, y, z));
                }
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                string[] p = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                // Triangolazione fan per poligoni con più di 3 vertici
                var faceIndices = new List<int>();
                for (int i = 1; i < p.Length; i++)
                {
                    // OBJ face index format: v, v/vt, v/vt/vn, v//vn
                    string idxStr = p[i].Split('/')[0];
                    if (int.TryParse(idxStr, out int idx))
                    {
                        // OBJ è 1-based; supporta indici negativi (relativi)
                        if (idx < 0) idx = vertices.Count + idx + 1;
                        faceIndices.Add(idx - 1);
                    }
                }

                // Fan triangulation
                for (int i = 1; i < faceIndices.Count - 1; i++)
                {
                    int i0 = faceIndices[0], i1 = faceIndices[i], i2 = faceIndices[i + 1];
                    if (i0 >= 0 && i0 < vertices.Count &&
                        i1 >= 0 && i1 < vertices.Count &&
                        i2 >= 0 && i2 < vertices.Count)
                    {
                        tris.Add(new Triangle(vertices[i0], vertices[i1], vertices[i2]));
                    }
                }
            }
        }

        if (tris.Count == 0) throw new IOException("OBJ: nessun triangolo valido trovato.");
        return tris;
    }

    // ── PLY (ASCII + Binary Little Endian) ──────────────────────────────────

    private static List<Triangle> LoadPly(string path)
    {
        byte[] allBytes = File.ReadAllBytes(path);
        int headerEnd = -1;
        for (int i = 0; i <= allBytes.Length - 10; i++)
        {
            if (allBytes[i] == (byte)'e' &&
                Encoding.ASCII.GetString(allBytes, i, 10) == "end_header")
            {
                int pos = i + 10;
                if (pos < allBytes.Length && allBytes[pos] == (byte)'\r') pos++;
                if (pos < allBytes.Length && allBytes[pos] == (byte)'\n') pos++;
                headerEnd = pos;
                break;
            }
        }

        if (headerEnd < 0) throw new IOException("PLY: end_header non trovato.");
        string headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
        bool isBinaryLE = headerText.Contains("binary_little_endian", StringComparison.OrdinalIgnoreCase);

        int vertexCount = 0, faceCount = 0;
        foreach (string rawLine in headerText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string h = rawLine.Trim();
            if (h.StartsWith("element vertex", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = h.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3) int.TryParse(parts[2], out vertexCount);
            }
            else if (h.StartsWith("element face", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = h.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3) int.TryParse(parts[2], out faceCount);
            }
        }

        if (vertexCount == 0) throw new IOException("PLY: nessun vertice dichiarato nell'header.");
        if (vertexCount > 10_000_000) throw new IOException("PLY: troppi vertici (limite 10 milioni).");

        var vertices = new List<Vec3>(vertexCount);
        var tris = new List<Triangle>();

        if (isBinaryLE)
        {
            using var ms = new MemoryStream(allBytes, headerEnd, allBytes.Length - headerEnd, writable: false);
            using var br = new BinaryReader(ms);
            for (int i = 0; i < vertexCount; i++)
            {
                float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
                vertices.Add(new Vec3(x, y, z));
            }
            for (int i = 0; i < faceCount; i++)
            {
                int n = br.ReadByte();
                var indices = new int[n];
                for (int j = 0; j < n; j++) indices[j] = br.ReadInt32();
                for (int j = 1; j < n - 1; j++)
                {
                    int i0 = indices[0], i1 = indices[j], i2 = indices[j + 1];
                    if (i0 >= 0 && i0 < vertices.Count && i1 >= 0 && i1 < vertices.Count && i2 >= 0 && i2 < vertices.Count)
                        tris.Add(new Triangle(vertices[i0], vertices[i1], vertices[i2]));
                }
            }
        }
        else
        {
            using var ms = new MemoryStream(allBytes, headerEnd, allBytes.Length - headerEnd, writable: false);
            using var reader = new StreamReader(ms, Encoding.UTF8);

            for (int i = 0; i < vertexCount; i++)
            {
                string? vLine = reader.ReadLine();
                if (vLine == null) break;
                string[] parts = vLine.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    vertices.Add(new Vec3(x, y, z));
                }
            }

            for (int i = 0; i < faceCount; i++)
            {
                string? fLine = reader.ReadLine();
                if (fLine == null) break;
                string[] parts = fLine.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[0], out int n) || n < 3) continue;

                var indices = new List<int>();
                for (int j = 1; j <= n && j < parts.Length; j++)
                {
                    if (int.TryParse(parts[j], out int idx) && idx >= 0 && idx < vertices.Count)
                        indices.Add(idx);
                }

                for (int j = 1; j < indices.Count - 1; j++)
                    tris.Add(new Triangle(vertices[indices[0]], vertices[indices[j]], vertices[indices[j + 1]]));
            }
        }

        if (tris.Count == 0) throw new IOException("PLY: nessun triangolo valido trovato.");
        return tris;
    }

    // ── GLB / GLTF 2.0 ──────────────────────────────────────────────────────

    private static List<Triangle> LoadGlbOrGltf(string path, string ext)
    {
        string jsonText;
        byte[]? binChunk = null;

        if (ext == "glb")
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            uint magic = br.ReadUInt32();
            if (magic != 0x46546C67u) // "glTF"
                throw new IOException("GLB non valido: magic header non riconosciuto.");
            uint version = br.ReadUInt32();
            uint totalLength = br.ReadUInt32();
            if (version != 2 || totalLength > fs.Length)
                throw new IOException("GLB non valido: versione o lunghezza errata.");

            uint jsonLen = br.ReadUInt32();
            uint jsonType = br.ReadUInt32();
            if (jsonType != 0x4E4F534Au) // "JSON"
                throw new IOException("GLB non valido: chunk JSON mancante.");
            if (jsonLen > fs.Length - fs.Position) throw new IOException("GLB non valido: versione o lunghezza errata.");
            byte[] jsonBytes = br.ReadBytes((int)jsonLen);
            jsonText = Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0', ' ');

            if (fs.Position + 8 <= fs.Length)
            {
                uint binLen = br.ReadUInt32();
                uint binType = br.ReadUInt32();
                if (binLen > fs.Length - fs.Position) throw new IOException("GLB non valido: versione o lunghezza errata.");
                if (binType == 0x004E4942u) // "BIN\0"
                    binChunk = br.ReadBytes((int)binLen);
            }
        }
        else
        {
            jsonText = File.ReadAllText(path, Encoding.UTF8);
        }

        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        var buffers = new List<byte[]>();
        if (root.TryGetProperty("buffers", out var buffersElem))
        {
            int idx = 0;
            foreach (var buf in buffersElem.EnumerateArray())
            {
                if (idx == 0 && binChunk != null && !buf.TryGetProperty("uri", out _))
                {
                    buffers.Add(binChunk);
                }
                else if (buf.TryGetProperty("uri", out var uriProp))
                {
                    string uri = uriProp.GetString() ?? "";
                    buffers.Add(ReadSafeGltfBufferUri(path, uri, MaxGltfBufferBytes));
                }
                else if (binChunk != null)
                {
                    buffers.Add(binChunk);
                }
                idx++;
            }
        }
        else if (binChunk != null)
        {
            buffers.Add(binChunk);
        }

        var bufferViews = root.GetProperty("bufferViews").EnumerateArray().ToArray();
        var accessors = root.GetProperty("accessors").EnumerateArray().ToArray();

        Vec3[] ReadPositions(int accIdx)
        {
            var acc = accessors[accIdx];
            int bvIdx = acc.GetProperty("bufferView").GetInt32();
            int count = acc.GetProperty("count").GetInt32();
            int accOffset = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;

            var bv = bufferViews[bvIdx];
            int bufIdx = bv.TryGetProperty("buffer", out var bi) ? bi.GetInt32() : 0;
            int bvOffset = bv.TryGetProperty("byteOffset", out var bvo) ? bvo.GetInt32() : 0;
            int stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : 12;

            byte[] buf = buffers[bufIdx];
            int start = bvOffset + accOffset;
            RequireGltfData(buf, start, count, stride, 12);
            var pts = new Vec3[count];
            for (int i = 0; i < count; i++)
            {
                int off = start + i * stride;
                float x = BitConverter.ToSingle(buf, off);
                float y = BitConverter.ToSingle(buf, off + 4);
                float z = BitConverter.ToSingle(buf, off + 8);
                pts[i] = new Vec3(x, y, z);
            }
            return pts;
        }

        int[] ReadIndices(int accIdx)
        {
            var acc = accessors[accIdx];
            int bvIdx = acc.GetProperty("bufferView").GetInt32();
            int count = acc.GetProperty("count").GetInt32();
            int compType = acc.GetProperty("componentType").GetInt32();
            int accOffset = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;

            var bv = bufferViews[bvIdx];
            int bufIdx = bv.TryGetProperty("buffer", out var bi) ? bi.GetInt32() : 0;
            int bvOffset = bv.TryGetProperty("byteOffset", out var bvo) ? bvo.GetInt32() : 0;

            byte[] buf = buffers[bufIdx];
            int start = bvOffset + accOffset;
            int width = compType switch { 5121 => 1, 5123 => 2, _ => 4 };
            RequireGltfData(buf, start, count, width, width);
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                indices[i] = compType switch
                {
                    5121 => buf[start + i],
                    5123 => BitConverter.ToUInt16(buf, start + i * 2),
                    5125 => (int)BitConverter.ToUInt32(buf, start + i * 4),
                    _ => throw new IOException($"GLTF: componentType indici non supportato ({compType}).")
                };
            }
            return indices;
        }

        var tris = new List<Triangle>();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                int posAcc = prim.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                Vec3[] verts = ReadPositions(posAcc);

                if (prim.TryGetProperty("indices", out var indProp))
                {
                    int[] indices = ReadIndices(indProp.GetInt32());
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                        tris.Add(new Triangle(verts[indices[i]], verts[indices[i + 1]], verts[indices[i + 2]]));
                }
                else
                {
                    for (int i = 0; i + 2 < verts.Length; i += 3)
                        tris.Add(new Triangle(verts[i], verts[i + 1], verts[i + 2]));
                }
            }
        }

        if (tris.Count == 0) throw new IOException($"{ext.ToUpperInvariant()}: nessun triangolo trovato.");
        return tris;
    }

    internal const long MaxGltfBufferBytes = 256L * 1024 * 1024;

    internal static byte[] ReadSafeGltfBufferUri(string gltfFilePath, string uri, long maxBufferBytes = MaxGltfBufferBytes)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidDataException("URI buffer glTF vuoto o non valido.");

        const string prefix = "data:application/octet-stream;base64,";
        const string prefixGltf = "data:application/gltf-buffer;base64,";
        string? base64Payload = uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? uri[prefix.Length..]
            : uri.StartsWith(prefixGltf, StringComparison.OrdinalIgnoreCase)
                ? uri[prefixGltf.Length..]
                : null;
        if (base64Payload != null)
        {
            long estimatedBytes = ((long)base64Payload.Length * 3) / 4;
            if (estimatedBytes > maxBufferBytes)
                throw new IOException($"Buffer glTF base64 oltre il limite massimo ({maxBufferBytes} byte).");
            byte[] decoded = Convert.FromBase64String(base64Payload);
            if (decoded.LongLength > maxBufferBytes)
                throw new IOException($"Buffer glTF base64 oltre il limite massimo ({maxBufferBytes} byte).");
            return decoded;
        }

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Schema data: buffer glTF non supportato.");

        string candidatePath = ResolveSafeGltfExternalPath(gltfFilePath, uri);
        var info = new FileInfo(candidatePath);
        if (!info.Exists)
            throw new FileNotFoundException("Buffer glTF esterno non trovato nella directory locale.", Path.GetFileName(candidatePath));
        if (info.Length > maxBufferBytes)
            throw new IOException($"Buffer glTF esterno superiore al limite consentito ({maxBufferBytes} byte).");

        return File.ReadAllBytes(candidatePath);
    }

    internal static string ResolveSafeGltfExternalPath(string gltfFilePath, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidDataException("Percorso buffer glTF esterno vuoto.");

        string unescaped;
        try { unescaped = Uri.UnescapeDataString(uri); }
        catch (UriFormatException) { throw new UnauthorizedAccessException("URI buffer glTF malformato."); }

        if (Uri.TryCreate(uri, UriKind.Absolute, out _) ||
            Uri.TryCreate(unescaped, UriKind.Absolute, out _) ||
            unescaped.Contains(':') ||
            unescaped.StartsWith('/') ||
            unescaped.StartsWith('\\') ||
            Path.IsPathRooted(unescaped))
        {
            throw new UnauthorizedAccessException("URI assoluti, schemi o percorsi di rete UNC non sono consentiti nei buffer glTF.");
        }

        string[] segments = unescaped.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(seg => seg == ".."))
            throw new UnauthorizedAccessException("Path traversal non consentito nei buffer glTF.");

        string baseDir = Path.GetFullPath(Path.GetDirectoryName(gltfFilePath) ?? ".");
        if ((new DirectoryInfo(baseDir).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("La directory sorgente glTF è un collegamento simbolico non consentito.");

        string relative = string.Join(Path.DirectorySeparatorChar, segments);
        string resolved = Path.GetFullPath(Path.Combine(baseDir, relative));
        string basePrefix = baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Il buffer glTF esterno deve trovarsi nella cartella del file .gltf.");

        string? currentDir = Path.GetDirectoryName(resolved);
        while (!string.IsNullOrEmpty(currentDir) &&
               !string.Equals(currentDir.TrimEnd(Path.DirectorySeparatorChar), baseDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(currentDir) && (new DirectoryInfo(currentDir).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Directory intermedia symlink non consentita per i buffer glTF.");
            currentDir = Path.GetDirectoryName(currentDir);
        }

        if (File.Exists(resolved))
        {
            var attrs = File.GetAttributes(resolved);
            if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Offline)) != 0)
                throw new UnauthorizedAccessException("Il buffer glTF esterno non può essere un symlink o reparse point.");
        }

        return resolved;
    }

    // ── 3MF (3D Manufacturing Format) ───────────────────────────────────────

    private static List<Triangle> Load3mf(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var modelEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("3MF non valido: file .model assente nell'archivio.");

        using var stream = modelEntry.Open();
        var doc = XDocument.Load(stream);
        var tris = new List<Triangle>();

        foreach (var meshElem in doc.Descendants().Where(e => e.Name.LocalName == "mesh"))
        {
            var verts = new List<Vec3>();
            foreach (var vElem in meshElem.Descendants().Where(e => e.Name.LocalName == "vertex"))
            {
                float x = ParseNumber(vElem.Attribute("x")?.Value ?? "0");
                float y = ParseNumber(vElem.Attribute("y")?.Value ?? "0");
                float z = ParseNumber(vElem.Attribute("z")?.Value ?? "0");
                verts.Add(new Vec3(x, y, z));
            }

            foreach (var tElem in meshElem.Descendants().Where(e => e.Name.LocalName == "triangle"))
            {
                int v1 = int.Parse(tElem.Attribute("v1")?.Value ?? "0", CultureInfo.InvariantCulture);
                int v2 = int.Parse(tElem.Attribute("v2")?.Value ?? "0", CultureInfo.InvariantCulture);
                int v3 = int.Parse(tElem.Attribute("v3")?.Value ?? "0", CultureInfo.InvariantCulture);
                if (v1 >= 0 && v1 < verts.Count && v2 >= 0 && v2 < verts.Count && v3 >= 0 && v3 < verts.Count)
                    tris.Add(new Triangle(verts[v1], verts[v2], verts[v3]));
            }
        }

        if (tris.Count == 0) throw new IOException("3MF: nessun triangolo valido trovato.");
        return tris;
    }

    // ── DAE (Collada XML) ───────────────────────────────────────────────────

    private static List<Triangle> LoadDae(string path)
    {
        var doc = XDocument.Load(path);
        var tris = new List<Triangle>();

        foreach (var mesh in doc.Descendants().Where(e => e.Name.LocalName == "mesh"))
        {
            var floatArrays = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var src in mesh.Elements().Where(e => e.Name.LocalName == "source"))
            {
                string srcId = src.Attribute("id")?.Value ?? "";
                var fa = src.Elements().FirstOrDefault(e => e.Name.LocalName == "float_array");
                if (fa != null)
                {
                    string[] tokens = fa.Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    float[] values = new float[tokens.Length];
                    for (int i = 0; i < tokens.Length; i++)
                        values[i] = float.Parse(tokens[i], CultureInfo.InvariantCulture);
                    floatArrays["#" + srcId] = values;
                }
            }

            var verticesElem = mesh.Elements().FirstOrDefault(e => e.Name.LocalName == "vertices");
            string verticesId = "#" + (verticesElem?.Attribute("id")?.Value ?? "");
            string posSourceUri = verticesElem?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "input" && e.Attribute("semantic")?.Value == "POSITION")
                ?.Attribute("source")?.Value ?? "";

            if (!floatArrays.TryGetValue(posSourceUri, out float[]? posFloats) && floatArrays.Count > 0)
                posFloats = floatArrays.Values.First();

            if (posFloats == null || posFloats.Length < 3) continue;

            var verts = new Vec3[posFloats.Length / 3];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = new Vec3(posFloats[i * 3], posFloats[i * 3 + 1], posFloats[i * 3 + 2]);

            foreach (var triGroup in mesh.Elements().Where(e => e.Name.LocalName is "triangles" or "polylist"))
            {
                var inputs = triGroup.Elements().Where(e => e.Name.LocalName == "input").ToList();
                int stride = inputs.Count > 0
                    ? inputs.Max(inp => int.TryParse(inp.Attribute("offset")?.Value, out int off) ? off : 0) + 1
                    : 1;
                int vertexOffset = 0;
                var vInput = inputs.FirstOrDefault(inp => inp.Attribute("semantic")?.Value == "VERTEX" || inp.Attribute("source")?.Value == verticesId);
                if (vInput != null && int.TryParse(vInput.Attribute("offset")?.Value, out int vo))
                    vertexOffset = vo;

                var pElem = triGroup.Elements().FirstOrDefault(e => e.Name.LocalName == "p");
                if (pElem == null) continue;
                string[] pTokens = pElem.Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                int[] pIndices = new int[pTokens.Length];
                for (int i = 0; i < pTokens.Length; i++)
                    pIndices[i] = int.Parse(pTokens[i], CultureInfo.InvariantCulture);

                if (triGroup.Name.LocalName == "triangles")
                {
                    int step = stride * 3;
                    for (int i = 0; i + step <= pIndices.Length; i += step)
                    {
                        int i0 = pIndices[i + vertexOffset];
                        int i1 = pIndices[i + stride + vertexOffset];
                        int i2 = pIndices[i + stride * 2 + vertexOffset];
                        if (i0 >= 0 && i0 < verts.Length && i1 >= 0 && i1 < verts.Length && i2 >= 0 && i2 < verts.Length)
                            tris.Add(new Triangle(verts[i0], verts[i1], verts[i2]));
                    }
                }
                else
                {
                    var vcountElem = triGroup.Elements().FirstOrDefault(e => e.Name.LocalName == "vcount");
                    if (vcountElem == null) continue;
                    string[] vcTokens = vcountElem.Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    int cursor = 0;
                    foreach (string vct in vcTokens)
                    {
                        int n = int.Parse(vct, CultureInfo.InvariantCulture);
                        if (cursor + n * stride > pIndices.Length) break;
                        int i0 = pIndices[cursor + vertexOffset];
                        for (int j = 1; j < n - 1; j++)
                        {
                            int i1 = pIndices[cursor + j * stride + vertexOffset];
                            int i2 = pIndices[cursor + (j + 1) * stride + vertexOffset];
                            if (i0 >= 0 && i0 < verts.Length && i1 >= 0 && i1 < verts.Length && i2 >= 0 && i2 < verts.Length)
                                tris.Add(new Triangle(verts[i0], verts[i1], verts[i2]));
                        }
                        cursor += n * stride;
                    }
                }
            }
        }

        if (tris.Count == 0) throw new IOException("DAE: nessun triangolo valido trovato.");
        return tris;
    }

    // ── FBX (ASCII 6.x/7.x + Binary Kaydara FBX v6.x/7.0–7.4 32-bit & v7.5+ 64-bit) ──

    private static List<Triangle> LoadFbx(string path)
    {
        byte[] header = new byte[27];
        using (var fs = File.OpenRead(path))
        {
            int read = fs.Read(header, 0, header.Length);
            if (read >= 20 && Encoding.ASCII.GetString(header, 0, 18).StartsWith("Kaydara FBX Binary", StringComparison.Ordinal))
                return LoadFbxBinary(path);
        }

        return LoadFbxAscii(path);
    }

    private sealed class FbxTransform
    {
        public float Tx, Ty, Tz;
        public float Rx, Ry, Rz;
        public float Sx = 1f, Sy = 1f, Sz = 1f;

        public bool IsIdentity =>
            MathF.Abs(Tx) < 1e-6f && MathF.Abs(Ty) < 1e-6f && MathF.Abs(Tz) < 1e-6f &&
            MathF.Abs(Rx) < 1e-6f && MathF.Abs(Ry) < 1e-6f && MathF.Abs(Rz) < 1e-6f &&
            MathF.Abs(Sx - 1f) < 1e-6f && MathF.Abs(Sy - 1f) < 1e-6f && MathF.Abs(Sz - 1f) < 1e-6f;

        public Vec3 Apply(Vec3 v)
        {
            float x = v.X * Sx;
            float y = v.Y * Sy;
            float z = v.Z * Sz;

            if (MathF.Abs(Rx) > 1e-6f)
            {
                float rad = Rx * MathF.PI / 180f;
                float c = MathF.Cos(rad), s = MathF.Sin(rad);
                (y, z) = (y * c - z * s, y * s + z * c);
            }
            if (MathF.Abs(Ry) > 1e-6f)
            {
                float rad = Ry * MathF.PI / 180f;
                float c = MathF.Cos(rad), s = MathF.Sin(rad);
                (x, z) = (x * c + z * s, -x * s + z * c);
            }
            if (MathF.Abs(Rz) > 1e-6f)
            {
                float rad = Rz * MathF.PI / 180f;
                float c = MathF.Cos(rad), s = MathF.Sin(rad);
                (x, y) = (x * c - y * s, x * s + y * c);
            }

            return new Vec3(x + Tx, y + Ty, z + Tz);
        }
    }

    private static void TriangulateFbxPolygonMesh(Vec3[] verts, int[] indices, FbxTransform? transform, List<Triangle> tris)
    {
        if (verts.Length == 0 || indices.Length < 3) return;
        Vec3[] working = verts;
        if (transform is { IsIdentity: false })
        {
            working = new Vec3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                working[i] = transform.Apply(verts[i]);
        }

        var poly = new List<int>(8);
        foreach (int idx in indices)
        {
            bool isEnd = idx < 0;
            int actual = isEnd ? ~idx : idx;
            poly.Add(actual);

            if (isEnd)
            {
                if (poly.Count >= 3)
                {
                    int i0 = poly[0];
                    for (int j = 1; j < poly.Count - 1; j++)
                    {
                        int i1 = poly[j], i2 = poly[j + 1];
                        if ((uint)i0 < (uint)working.Length &&
                            (uint)i1 < (uint)working.Length &&
                            (uint)i2 < (uint)working.Length)
                        {
                            tris.Add(new Triangle(working[i0], working[i1], working[i2]));
                        }
                    }
                }
                poly.Clear();
            }
        }
    }

    private static List<Triangle> LoadFbxAscii(string path)
    {
        string content = File.ReadAllText(path, Encoding.UTF8);
        var tris = new List<Triangle>();

        // Supporta sia FBX 7.x ("Vertices: *N { a: ... }") sia FBX 6.x ("Vertices: x, y, z...")
        var vertRegex = new Regex(@"Vertices:\s*(?:\*\d+\s*\{\s*a:\s*([0-9eE\.\+\-,\s]+)\}|([0-9eE\.\+\-,\s]+))", RegexOptions.Singleline);
        var polyRegex = new Regex(@"PolygonVertexIndex:\s*(?:\*\d+\s*\{\s*a:\s*([0-9\+\-,\s]+)\}|([0-9\+\-,\s]+))", RegexOptions.Singleline);

        var polyMatches = polyRegex.Matches(content);
        var vertMatches = vertRegex.Matches(content);

        foreach (Match pm in polyMatches)
        {
            // Associa ogni PolygonVertexIndex al blocco Vertices immediatamente precedente nello stesso nodo Geometry/Model
            Match? bestVert = null;
            for (int v = vertMatches.Count - 1; v >= 0; v--)
            {
                if (vertMatches[v].Index < pm.Index)
                {
                    bestVert = vertMatches[v];
                    break;
                }
            }
            bestVert ??= vertMatches.Count > 0 ? vertMatches[0] : null;
            if (bestVert == null) continue;

            string rawV = bestVert.Groups[1].Success ? bestVert.Groups[1].Value : bestVert.Groups[2].Value;
            string rawI = pm.Groups[1].Success ? pm.Groups[1].Value : pm.Groups[2].Value;

            string[] vTokens = rawV.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string[] iTokens = rawI.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            var verts = new Vec3[vTokens.Length / 3];
            for (int i = 0; i < verts.Length; i++)
            {
                if (float.TryParse(vTokens[i * 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(vTokens[i * 3 + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(vTokens[i * 3 + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    verts[i] = new Vec3(x, y, z);
                }
            }

            var indices = new List<int>(iTokens.Length);
            foreach (string tok in iTokens)
            {
                if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                    indices.Add(idx);
            }

            TriangulateFbxPolygonMesh(verts, indices.ToArray(), null, tris);
        }

        if (tris.Count == 0) throw new IOException("FBX ASCII: nessun triangolo valido trovato.");
        return tris;
    }

    private static List<Triangle> LoadFbxBinary(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (fs.Length < 27) throw new IOException("FBX binario troncato o non valido.");

        fs.Position = 23;
        uint version = br.ReadUInt32();
        bool is64 = version >= 7500;
        int headerSize = is64 ? 25 : 13;

        var geometries = new List<(long Id, Vec3[] Verts, int[] Polys, FbxTransform? DirectTransform)>();
        var models = new Dictionary<long, FbxTransform>();
        var childToParents = new Dictionary<long, List<long>>();

        void ScanNodes(long endLimit, out Vec3[]? nodeVerts, out int[]? nodePolys, FbxTransform? activeTransform)
        {
            nodeVerts = null;
            nodePolys = null;
            FbxTransform? localTransform = activeTransform;

            while (fs.Position + headerSize <= endLimit && fs.Position + headerSize <= fs.Length)
            {
                ulong endOffset = is64 ? br.ReadUInt64() : br.ReadUInt32();
                ulong numProps = is64 ? br.ReadUInt64() : br.ReadUInt32();
                ulong propListLen = is64 ? br.ReadUInt64() : br.ReadUInt32();
                byte nameLen = br.ReadByte();

                if (endOffset == 0 && numProps == 0 && propListLen == 0 && nameLen == 0)
                    break;

                string name = nameLen > 0 ? Encoding.ASCII.GetString(br.ReadBytes(nameLen)) : "";
                long propsStart = fs.Position;
                long propsEnd = propsStart + (long)propListLen;
                long nodeEnd = (long)endOffset;

                if (propsEnd > fs.Length || nodeEnd > fs.Length || nodeEnd < propsEnd)
                    break;

                long nodeId = 0;
                if (name is "Geometry" or "Model" && numProps >= 1 && propListLen > 0)
                {
                    if (TryReadFbxScalarAsDouble(br, propsEnd, out double idVal))
                        nodeId = (long)Math.Round(idVal);
                    fs.Position = propsStart;
                }
                else if (name == "C" && numProps >= 3)
                {
                    TryReadFbxConnection(br, propsEnd, childToParents);
                }
                else if (name == "Vertices" && numProps >= 1)
                {
                    try
                    {
                        double[] arr = ReadFbxDoubleValues(br, (int)numProps, propsEnd);
                        if (arr.Length >= 3)
                        {
                            var verts = new Vec3[arr.Length / 3];
                            for (int i = 0; i < verts.Length; i++)
                                verts[i] = new Vec3((float)arr[i * 3], (float)arr[i * 3 + 1], (float)arr[i * 3 + 2]);
                            nodeVerts = verts;
                        }
                    }
                    catch { }
                }
                else if (name == "PolygonVertexIndex" && numProps >= 1)
                {
                    try
                    {
                        int[] indices = ReadFbxIntValues(br, (int)numProps, propsEnd);
                        if (indices.Length >= 3)
                            nodePolys = indices;
                    }
                    catch { }
                }
                else if (name is "P" or "Property" && numProps >= 4)
                {
                    try
                    {
                        localTransform ??= new FbxTransform();
                        TryReadFbxPropertyTransform(br, (int)numProps, propsEnd, localTransform);
                    }
                    catch { }
                }

                fs.Position = propsEnd;
                if (nodeEnd > fs.Position)
                {
                    var childTransform = name == "Model" ? new FbxTransform() : localTransform;
                    ScanNodes(nodeEnd, out var childVerts, out var childPolys, childTransform);
                    if (childVerts != null && childPolys != null)
                        geometries.Add((nodeId, childVerts, childPolys, childTransform));
                    else if (name == "Model" && nodeId != 0 && childTransform is { IsIdentity: false })
                        models[nodeId] = childTransform;
                }

                fs.Position = nodeEnd;
            }
        }

        ScanNodes(fs.Length, out var rootVerts, out var rootPolys, null);
        if (rootVerts != null && rootPolys != null)
            geometries.Add((0, rootVerts, rootPolys, null));

        var tris = new List<Triangle>();
        foreach (var (geomId, verts, polys, directTf) in geometries)
        {
            var chain = ResolveModelTransformChain(geomId, directTf, models, childToParents);
            if (chain.Count <= 1)
            {
                TriangulateFbxPolygonMesh(verts, polys, chain.Count == 1 ? chain[0] : null, tris);
                continue;
            }

            // Applica la gerarchia Model -> parent Model dal locale verso la radice
            var transformed = (Vec3[])verts.Clone();
            foreach (var tf in chain)
            {
                if (tf.IsIdentity) continue;
                for (int i = 0; i < transformed.Length; i++)
                    transformed[i] = tf.Apply(transformed[i]);
            }
            TriangulateFbxPolygonMesh(transformed, polys, null, tris);
        }

        if (tris.Count == 0) throw new IOException("FBX binario: nessun triangolo valido trovato.");
        return tris;
    }

    private static void TryReadFbxConnection(BinaryReader br, long propsEnd, Dictionary<long, List<long>> childToParents)
    {
        try
        {
            if (br.BaseStream.Position >= propsEnd || (char)br.ReadByte() != 'S') return;
            uint connLen = br.ReadUInt32();
            if (br.BaseStream.Position + connLen > propsEnd) return;
            string connType = Encoding.ASCII.GetString(br.ReadBytes((int)connLen));
            if (connType != "OO") return;
            if (!TryReadFbxScalarAsDouble(br, propsEnd, out double childIdRaw)) return;
            if (!TryReadFbxScalarAsDouble(br, propsEnd, out double parentIdRaw)) return;
            long childId = (long)Math.Round(childIdRaw);
            long parentId = (long)Math.Round(parentIdRaw);
            if (childId == 0 || parentId == 0) return;
            if (!childToParents.TryGetValue(childId, out var parents))
            {
                parents = [];
                childToParents[childId] = parents;
            }
            parents.Add(parentId);
        }
        catch { }
    }

    private static List<FbxTransform> ResolveModelTransformChain(
        long geomId,
        FbxTransform? directTf,
        Dictionary<long, FbxTransform> models,
        Dictionary<long, List<long>> childToParents)
    {
        var chain = new List<FbxTransform>();
        if (directTf is { IsIdentity: false })
            chain.Add(directTf);

        if (geomId == 0 || !childToParents.TryGetValue(geomId, out var directParents))
            return chain;

        long current = directParents.FirstOrDefault(models.ContainsKey);
        if (current == 0) current = directParents[0];
        var visited = new HashSet<long>();
        while (current != 0 && visited.Add(current))
        {
            if (models.TryGetValue(current, out var tf) && !tf.IsIdentity)
                chain.Add(tf);
            current = childToParents.TryGetValue(current, out var ancestors) ? ancestors[0] : 0;
        }
        return chain;
    }

    /// <summary>I conteggi scritti nel file non devono far riservare memoria per dati che non ci sono.</summary>
    private static void RequireGltfData(byte[] buffer, int start, int count, int stride, int size)
    {
        if (start < 0 || count < 0 || stride < 0 || (count > 0 && start + (count - 1L) * stride + size > buffer.Length) || (stride == 0 && count > buffer.Length))
            throw new IOException("GLTF: dati incompleti.");
    }

    private static byte[] DecompressFbxArray(byte[] raw, int expectedBytes)
    {
        if (raw.Length == 0 || expectedBytes <= 0) return [];
        // DEFLATE non supera 1032:1.
        if (expectedBytes / 1032 > raw.Length) throw new IOException("FBX: dati incompleti.");

        // 1. Salta i 2 byte dell'header zlib (CMF + FLG) e decomprimi con DeflateStream
        if (raw.Length > 2)
        {
            try
            {
                using var ms = new MemoryStream(raw, 2, raw.Length - 2, writable: false);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                byte[] unpacked = new byte[expectedBytes];
                int total = 0;
                while (total < expectedBytes)
                {
                    int read = deflate.Read(unpacked, total, expectedBytes - total);
                    if (read <= 0) break;
                    total += read;
                }
                if (total == expectedBytes) return unpacked;
            }
            catch { }
        }

        // 2. Fallback con ZLibStream standard
        using (var ms = new MemoryStream(raw, writable: false))
        using (var zlib = new ZLibStream(ms, CompressionMode.Decompress))
        {
            byte[] unpacked = new byte[expectedBytes];
            zlib.ReadExactly(unpacked);
            return unpacked;
        }
    }

    private static byte[] ReadFbxRawArrayBytes(BinaryReader br, int elemSize, out uint length)
    {
        _ = (char)br.ReadByte();
        length = br.ReadUInt32();
        uint encoding = br.ReadUInt32();
        uint compLen = br.ReadUInt32();
        if (compLen > br.BaseStream.Length - br.BaseStream.Position) throw new IOException("FBX: dati incompleti.");
        byte[] raw = br.ReadBytes((int)compLen);
        int expectedBytes = checked((int)(length * (uint)elemSize));
        return encoding == 1 ? DecompressFbxArray(raw, expectedBytes) : raw;
    }

    private static double[] ReadFbxDoubleValues(BinaryReader br, int numProps, long propsEnd)
    {
        long pos = br.BaseStream.Position;
        char type = (char)br.ReadByte();
        br.BaseStream.Position = pos;

        if (type == 'f')
        {
            byte[] bytes = ReadFbxRawArrayBytes(br, 4, out uint len);
            int count = Math.Min((int)len, bytes.Length / 4);
            var res = new double[count];
            for (int i = 0; i < count; i++) res[i] = BitConverter.ToSingle(bytes, i * 4);
            return res;
        }
        if (type == 'd')
        {
            byte[] bytes = ReadFbxRawArrayBytes(br, 8, out uint len);
            int count = Math.Min((int)len, bytes.Length / 8);
            var res = new double[count];
            for (int i = 0; i < count; i++) res[i] = BitConverter.ToDouble(bytes, i * 8);
            return res;
        }

        // FBX 6.x / 7.0: lista di proprietà scalari ('D' o 'F')
        var scalars = new List<double>(numProps);
        for (int i = 0; i < numProps && br.BaseStream.Position < propsEnd; i++)
        {
            if (TryReadFbxScalarAsDouble(br, propsEnd, out double val))
                scalars.Add(val);
            else
                break;
        }
        return scalars.ToArray();
    }

    private static int[] ReadFbxIntValues(BinaryReader br, int numProps, long propsEnd)
    {
        long pos = br.BaseStream.Position;
        char type = (char)br.ReadByte();
        br.BaseStream.Position = pos;

        if (type == 'i')
        {
            byte[] bytes = ReadFbxRawArrayBytes(br, 4, out uint len);
            int count = Math.Min((int)len, bytes.Length / 4);
            var res = new int[count];
            for (int i = 0; i < count; i++) res[i] = BitConverter.ToInt32(bytes, i * 4);
            return res;
        }
        if (type == 'l')
        {
            byte[] bytes = ReadFbxRawArrayBytes(br, 8, out uint len);
            int count = Math.Min((int)len, bytes.Length / 8);
            var res = new int[count];
            for (int i = 0; i < count; i++) res[i] = (int)BitConverter.ToInt64(bytes, i * 8);
            return res;
        }

        // FBX 6.x / 7.0: lista di proprietà scalari ('I' o 'L')
        var scalars = new List<int>(numProps);
        for (int i = 0; i < numProps && br.BaseStream.Position < propsEnd; i++)
        {
            if (TryReadFbxScalarAsDouble(br, propsEnd, out double val))
                scalars.Add((int)Math.Round(val));
            else
                break;
        }
        return scalars.ToArray();
    }

    private static bool TryReadFbxScalarAsDouble(BinaryReader br, long propsEnd, out double value)
    {
        value = 0;
        if (br.BaseStream.Position >= propsEnd) return false;
        char t = (char)br.ReadByte();
        switch (t)
        {
            case 'D': value = br.ReadDouble(); return true;
            case 'F': value = br.ReadSingle(); return true;
            case 'I': value = br.ReadInt32(); return true;
            case 'L': value = br.ReadInt64(); return true;
            case 'Y': value = br.ReadInt16(); return true;
            case 'C': value = br.ReadByte(); return true;
            case 'S' or 'R':
                uint len = br.ReadUInt32();
                br.BaseStream.Position = Math.Min(propsEnd, br.BaseStream.Position + len);
                return false;
            default:
                return false;
        }
    }

    private static void TryReadFbxPropertyTransform(BinaryReader br, int numProps, long propsEnd, FbxTransform target)
    {
        if (br.BaseStream.Position >= propsEnd || (char)br.ReadByte() != 'S') return;
        uint nameLen = br.ReadUInt32();
        if (br.BaseStream.Position + nameLen > propsEnd) return;
        string propName = Encoding.UTF8.GetString(br.ReadBytes((int)nameLen));
        if (propName is not ("Lcl Translation" or "Lcl Rotation" or "Lcl Scaling")) return;

        var nums = new List<double>(3);
        for (int i = 1; i < numProps && br.BaseStream.Position < propsEnd; i++)
        {
            if (TryReadFbxScalarAsDouble(br, propsEnd, out double v))
                nums.Add(v);
        }
        if (nums.Count < 3) return;
        float x = (float)nums[^3], y = (float)nums[^2], z = (float)nums[^1];
        if (propName == "Lcl Translation") { target.Tx = x; target.Ty = y; target.Tz = z; }
        else if (propName == "Lcl Rotation") { target.Rx = x; target.Ry = y; target.Rz = z; }
        else if (propName == "Lcl Scaling" && MathF.Abs(x) > 1e-6f && MathF.Abs(y) > 1e-6f && MathF.Abs(z) > 1e-6f)
        {
            target.Sx = x; target.Sy = y; target.Sz = z;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MESH WRITERS
    // ══════════════════════════════════════════════════════════════════════════

    private static void WriteMesh(List<Triangle> tris, string path, string ext)
    {
        switch (ext)
        {
            case "stl": WriteStlAscii(tris, path); break;
            case "obj": WriteObj(tris, path); break;
            case "ply": WritePly(tris, path); break;
            case "glb": WriteGlb(tris, path); break;
            case "gltf": WriteGltf(tris, path); break;
            case "3mf": Write3mf(tris, path); break;
            case "dae": WriteDae(tris, path); break;
            case "fbx": WriteFbxAscii(tris, path); break;
            default: throw new IOException($"Formato mesh di output non supportato: {ext}");
        }
    }

    private static void WriteStlAscii(List<Triangle> tris, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("solid mesh");
        foreach (var t in tris)
        {
            float ux = t.B.X - t.A.X, uy = t.B.Y - t.A.Y, uz = t.B.Z - t.A.Z;
            float vx = t.C.X - t.A.X, vy = t.C.Y - t.A.Y, vz = t.C.Z - t.A.Z;
            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-9f) { nx /= len; ny /= len; nz /= len; }

            sb.AppendLine(FormattableString.Invariant($"  facet normal {nx:G6} {ny:G6} {nz:G6}"));
            sb.AppendLine("    outer loop");
            sb.AppendLine(FormattableString.Invariant($"      vertex {t.A.X:G9} {t.A.Y:G9} {t.A.Z:G9}"));
            sb.AppendLine(FormattableString.Invariant($"      vertex {t.B.X:G9} {t.B.Y:G9} {t.B.Z:G9}"));
            sb.AppendLine(FormattableString.Invariant($"      vertex {t.C.X:G9} {t.C.Y:G9} {t.C.Z:G9}"));
            sb.AppendLine("    endloop");
            sb.AppendLine("  endfacet");
        }
        sb.AppendLine("endsolid mesh");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteObj(List<Triangle> tris, string path)
    {
        var (uniqueVerts, faceIndices) = BuildIndexedMesh(tris);

        var sb = new StringBuilder();
        sb.AppendLine("# Wavefront OBJ - Generated by FLIP");
        sb.AppendLine(FormattableString.Invariant($"# Vertices: {uniqueVerts.Count}, Faces: {faceIndices.Count}"));

        foreach (var v in uniqueVerts)
            sb.AppendLine(FormattableString.Invariant($"v {v.X:G9} {v.Y:G9} {v.Z:G9}"));

        foreach (var (a, b, c) in faceIndices)
            sb.AppendLine(FormattableString.Invariant($"f {a + 1} {b + 1} {c + 1}"));

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WritePly(List<Triangle> tris, string path)
    {
        var (uniqueVerts, faceIndices) = BuildIndexedMesh(tris);

        var sb = new StringBuilder();
        sb.AppendLine("ply");
        sb.AppendLine("format ascii 1.0");
        sb.AppendLine(FormattableString.Invariant($"element vertex {uniqueVerts.Count}"));
        sb.AppendLine("property float x");
        sb.AppendLine("property float y");
        sb.AppendLine("property float z");
        sb.AppendLine(FormattableString.Invariant($"element face {faceIndices.Count}"));
        sb.AppendLine("property list uchar int vertex_indices");
        sb.AppendLine("end_header");

        foreach (var v in uniqueVerts)
            sb.AppendLine(FormattableString.Invariant($"{v.X:G9} {v.Y:G9} {v.Z:G9}"));

        foreach (var (a, b, c) in faceIndices)
            sb.AppendLine(FormattableString.Invariant($"3 {a} {b} {c}"));

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static (byte[] BinBuffer, string JsonText) BuildGltfPayload(List<Triangle> tris, bool embedBase64Uri)
    {
        var (verts, faces) = BuildIndexedMesh(tris);
        int vByteLen = verts.Count * 12;
        int iByteLen = faces.Count * 12;
        int totalBin = vByteLen + iByteLen;
        while (totalBin % 4 != 0) totalBin++;

        byte[] bin = new byte[totalBin];
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        for (int i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            if (v.X < minX) minX = v.X; if (v.Y < minY) minY = v.Y; if (v.Z < minZ) minZ = v.Z;
            if (v.X > maxX) maxX = v.X; if (v.Y > maxY) maxY = v.Y; if (v.Z > maxZ) maxZ = v.Z;
            BitConverter.GetBytes(v.X).CopyTo(bin, i * 12);
            BitConverter.GetBytes(v.Y).CopyTo(bin, i * 12 + 4);
            BitConverter.GetBytes(v.Z).CopyTo(bin, i * 12 + 8);
        }

        for (int i = 0; i < faces.Count; i++)
        {
            var (a, b, c) = faces[i];
            int off = vByteLen + i * 12;
            BitConverter.GetBytes((uint)a).CopyTo(bin, off);
            BitConverter.GetBytes((uint)b).CopyTo(bin, off + 4);
            BitConverter.GetBytes((uint)c).CopyTo(bin, off + 8);
        }

        string bufferEntry = embedBase64Uri
            ? FormattableString.Invariant($"{{\"byteLength\":{totalBin},\"uri\":\"data:application/octet-stream;base64,{Convert.ToBase64String(bin)}\"}}")
            : FormattableString.Invariant($"{{\"byteLength\":{totalBin}}}");

        string json = FormattableString.Invariant($$"""
            {
              "asset": { "version": "2.0", "generator": "FLIP 3D Engine" },
              "scene": 0,
              "scenes": [{ "nodes": [0] }],
              "nodes": [{ "mesh": 0 }],
              "meshes": [{
                "primitives": [{
                  "attributes": { "POSITION": 0 },
                  "indices": 1,
                  "mode": 4
                }]
              }],
              "buffers": [{{bufferEntry}}],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": {{vByteLen}}, "target": 34962 },
                { "buffer": 0, "byteOffset": {{vByteLen}}, "byteLength": {{iByteLen}}, "target": 34963 }
              ],
              "accessors": [
                {
                  "bufferView": 0,
                  "componentType": 5126,
                  "count": {{verts.Count}},
                  "type": "VEC3",
                  "min": [{{minX:G9}}, {{minY:G9}}, {{minZ:G9}}],
                  "max": [{{maxX:G9}}, {{maxY:G9}}, {{maxZ:G9}}]
                },
                {
                  "bufferView": 1,
                  "componentType": 5125,
                  "count": {{faces.Count * 3}},
                  "type": "SCALAR"
                }
              ]
            }
            """);

        return (bin, json);
    }

    private static void WriteGlb(List<Triangle> tris, string path)
    {
        var (bin, json) = BuildGltfPayload(tris, embedBase64Uri: false);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int jsonChunkLen = jsonBytes.Length + jsonPad;
        int binChunkLen = bin.Length;
        int totalLength = 12 + 8 + jsonChunkLen + 8 + binChunkLen;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x46546C67u); // "glTF"
        bw.Write(2u);          // version 2
        bw.Write((uint)totalLength);

        bw.Write((uint)jsonChunkLen);
        bw.Write(0x4E4F534Au); // "JSON"
        bw.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) bw.Write((byte)0x20);

        bw.Write((uint)binChunkLen);
        bw.Write(0x004E4942u); // "BIN\0"
        bw.Write(bin);
    }

    private static void WriteGltf(List<Triangle> tris, string path)
    {
        var (_, json) = BuildGltfPayload(tris, embedBase64Uri: true);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static void Write3mf(List<Triangle> tris, string path)
    {
        var (verts, faces) = BuildIndexedMesh(tris);
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var ctEntry = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
        using (var sw = new StreamWriter(ctEntry.Open(), new UTF8Encoding(false)))
        {
            sw.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/>
                </Types>
                """);
        }

        var relsEntry = zip.CreateEntry("_rels/.rels", CompressionLevel.Optimal);
        using (var sw = new StreamWriter(relsEntry.Open(), new UTF8Encoding(false)))
        {
            sw.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/>
                </Relationships>
                """);
        }

        var modelEntry = zip.CreateEntry("3D/3dmodel.model", CompressionLevel.Optimal);
        using (var sw = new StreamWriter(modelEntry.Open(), new UTF8Encoding(false)))
        {
            sw.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sw.WriteLine("<model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">");
            sw.WriteLine("  <resources>");
            sw.WriteLine("    <object id=\"1\" type=\"model\">");
            sw.WriteLine("      <mesh>");
            sw.WriteLine("        <vertices>");
            foreach (var v in verts)
                sw.WriteLine(FormattableString.Invariant($"          <vertex x=\"{v.X:G9}\" y=\"{v.Y:G9}\" z=\"{v.Z:G9}\"/>"));
            sw.WriteLine("        </vertices>");
            sw.WriteLine("        <triangles>");
            foreach (var (a, b, c) in faces)
                sw.WriteLine(FormattableString.Invariant($"          <triangle v1=\"{a}\" v2=\"{b}\" v3=\"{c}\"/>"));
            sw.WriteLine("        </triangles>");
            sw.WriteLine("      </mesh>");
            sw.WriteLine("    </object>");
            sw.WriteLine("  </resources>");
            sw.WriteLine("  <build>");
            sw.WriteLine("    <item objectid=\"1\"/>");
            sw.WriteLine("  </build>");
            sw.WriteLine("</model>");
        }
    }

    private static void WriteDae(List<Triangle> tris, string path)
    {
        var (verts, faces) = BuildIndexedMesh(tris);
        var posSb = new StringBuilder();
        for (int i = 0; i < verts.Count; i++)
        {
            if (i > 0) posSb.Append(' ');
            posSb.Append(FormattableString.Invariant($"{verts[i].X:G9} {verts[i].Y:G9} {verts[i].Z:G9}"));
        }

        var idxSb = new StringBuilder();
        for (int i = 0; i < faces.Count; i++)
        {
            if (i > 0) idxSb.Append(' ');
            idxSb.Append(FormattableString.Invariant($"{faces[i].A} {faces[i].B} {faces[i].C}"));
        }

        string xml = FormattableString.Invariant($$"""
            <?xml version="1.0" encoding="utf-8"?>
            <COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
              <asset><unit name="meter" meter="1"/><up_axis>Y_UP</up_axis></asset>
              <library_geometries>
                <geometry id="flip-mesh" name="FlipMesh">
                  <mesh>
                    <source id="flip-positions">
                      <float_array id="flip-positions-array" count="{{verts.Count * 3}}">{{posSb}}</float_array>
                      <technique_common>
                        <accessor source="#flip-positions-array" count="{{verts.Count}}" stride="3">
                          <param name="X" type="float"/>
                          <param name="Y" type="float"/>
                          <param name="Z" type="float"/>
                        </accessor>
                      </technique_common>
                    </source>
                    <vertices id="flip-vertices">
                      <input semantic="POSITION" source="#flip-positions"/>
                    </vertices>
                    <triangles count="{{faces.Count}}">
                      <input semantic="VERTEX" source="#flip-vertices" offset="0"/>
                      <p>{{idxSb}}</p>
                    </triangles>
                  </mesh>
                </geometry>
              </library_geometries>
              <library_visual_scenes>
                <visual_scene id="Scene" name="Scene">
                  <node id="FlipNode" name="FlipNode" type="NODE">
                    <instance_geometry url="#flip-mesh"/>
                  </node>
                </visual_scene>
              </library_visual_scenes>
              <scene>
                <instance_visual_scene url="#Scene"/>
              </scene>
            </COLLADA>
            """);

        File.WriteAllText(path, xml, new UTF8Encoding(false));
    }

    private static void WriteFbxAscii(List<Triangle> tris, string path)
    {
        var (verts, faces) = BuildIndexedMesh(tris);
        var vSb = new StringBuilder();
        for (int i = 0; i < verts.Count; i++)
        {
            if (i > 0) vSb.Append(',');
            vSb.Append(FormattableString.Invariant($"{verts[i].X:G9},{verts[i].Y:G9},{verts[i].Z:G9}"));
        }

        var fSb = new StringBuilder();
        for (int i = 0; i < faces.Count; i++)
        {
            if (i > 0) fSb.Append(',');
            int negLast = ~(faces[i].C);
            fSb.Append(FormattableString.Invariant($"{faces[i].A},{faces[i].B},{negLast}"));
        }

        string fbx = FormattableString.Invariant($$"""
            ; FBX 7.4.0 project file
            ; Generated by FLIP 3D Engine
            FBXHeaderExtension:  {
                FBXHeaderVersion: 1003
                FBXVersion: 7400
            }
            Objects:  {
                Geometry: 1000, "Geometry::FlipMesh", "Mesh" {
                    Vertices: *{{verts.Count * 3}} {
                        a: {{vSb}}
                    }
                    PolygonVertexIndex: *{{faces.Count * 3}} {
                        a: {{fSb}}
                    }
                    GeometryVersion: 124
                }
                Model: 2000, "Model::FlipModel", "Mesh" {
                    Version: 232
                }
            }
            Connections:  {
                C: "OO",1000,2000
                C: "OO",2000,0
            }
            """);

        File.WriteAllText(path, fbx, new UTF8Encoding(false));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MESH ↔ MESH
    // ══════════════════════════════════════════════════════════════════════════

    private static void ConvertMesh(string srcPath, string srcExt, string dstPath, string dstExt)
    {
        var tris = LoadMesh(srcPath, srcExt);
        WriteMesh(tris, dstPath, dstExt);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MESH → BLUEPRINT TECNICO (4 VISTE + CARTIGLIO)
    // ══════════════════════════════════════════════════════════════════════════

    private static void DrawMeshBlueprintCanvas(SKCanvas canvas, int width, int height, string fileName, List<Triangle> tris)
    {
        var (uniqueVerts, faces) = BuildIndexedMesh(tris);

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var v in uniqueVerts)
        {
            if (v.X < minX) minX = v.X; if (v.Y < minY) minY = v.Y; if (v.Z < minZ) minZ = v.Z;
            if (v.X > maxX) maxX = v.X; if (v.Y > maxY) maxY = v.Y; if (v.Z > maxZ) maxZ = v.Z;
        }
        float sizeX = Math.Max(maxX - minX, 1e-5f);
        float sizeY = Math.Max(maxY - minY, 1e-5f);
        float sizeZ = Math.Max(maxZ - minZ, 1e-5f);

        canvas.Clear(new SKColor(15, 20, 32));

        using var gridPaint = new SKPaint { Color = new SKColor(28, 38, 58), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        for (int x = 0; x < width; x += 40) canvas.DrawLine(x, 0, x, height, gridPaint);
        for (int y = 0; y < height; y += 40) canvas.DrawLine(0, y, width, y, gridPaint);

        using var borderPaint = new SKPaint { Color = new SKColor(255, 122, 24), StrokeWidth = 2.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var panelBorderPaint = new SKPaint { Color = new SKColor(58, 76, 110), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var wirePaint = new SKPaint { Color = new SKColor(76, 201, 240, 210), StrokeWidth = 1.2f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var titleFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 18);
        using var labelFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 14);
        using var metaFont = new SKFont(SKTypeface.FromFamilyName("Consolas", SKFontStyle.Normal), 14);
        using var textPaint = new SKPaint { Color = new SKColor(235, 240, 250), IsAntialias = true };
        using var accentTextPaint = new SKPaint { Color = new SKColor(255, 122, 24), IsAntialias = true };

        float margin = 24;
        float footerH = 88;
        float availW = width - margin * 2;
        float availH = height - margin * 2 - footerH - 12;
        float cellW = (availW - 16) / 2f;
        float cellH = (availH - 16) / 2f;

        void RenderQuadrant(SKRect rect, string label, Func<Vec3, (float X, float Y, float Depth)> proj, bool shaded)
        {
            canvas.DrawRect(rect, panelBorderPaint);
            canvas.DrawText(label, rect.Left + 14, rect.Top + 24, SKTextAlign.Left, labelFont, accentTextPaint);

            float pMinX = float.MaxValue, pMinY = float.MaxValue, pMaxX = float.MinValue, pMaxY = float.MinValue;
            foreach (var v in uniqueVerts)
            {
                var (px, py, _) = proj(v);
                if (px < pMinX) pMinX = px; if (py < pMinY) pMinY = py;
                if (px > pMaxX) pMaxX = px; if (py > pMaxY) pMaxY = py;
            }
            float spanX = Math.Max(pMaxX - pMinX, 1e-5f);
            float spanY = Math.Max(pMaxY - pMinY, 1e-5f);
            float pad = 36f;
            float scale = Math.Min((rect.Width - pad * 2) / spanX, (rect.Height - pad * 2 - 20) / spanY);
            float cx = rect.Left + rect.Width / 2f;
            float cy = rect.Top + 14 + rect.Height / 2f;
            float midX = (pMinX + pMaxX) / 2f;
            float midY = (pMinY + pMaxY) / 2f;

            SKPoint Map(float px, float py) => new(cx + (px - midX) * scale, cy + (py - midY) * scale);

            var orderedTris = shaded
                ? tris.OrderBy(t => (proj(t.A).Depth + proj(t.B).Depth + proj(t.C).Depth) / 3f).ToList()
                : tris;

            foreach (var t in orderedTris)
            {
                var pa = proj(t.A); var pb = proj(t.B); var pc = proj(t.C);
                var p0 = Map(pa.X, pa.Y);
                var p1 = Map(pb.X, pb.Y);
                var p2 = Map(pc.X, pc.Y);

                using var pbld = new SKPathBuilder();
                pbld.MoveTo(p0); pbld.LineTo(p1); pbld.LineTo(p2); pbld.Close();
                using var path = pbld.Detach();

                if (shaded)
                {
                    float ux = t.B.X - t.A.X, uy = t.B.Y - t.A.Y, uz = t.B.Z - t.A.Z;
                    float vx = t.C.X - t.A.X, vy = t.C.Y - t.A.Y, vz = t.C.Z - t.A.Z;
                    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    float nLen = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nLen > 1e-6f) { nx /= nLen; ny /= nLen; nz /= nLen; }
                    float light = Math.Clamp(MathF.Abs(nx * 0.55f + ny * 0.65f + nz * 0.52f), 0.2f, 1.0f);
                    byte r = (byte)(35 + 185 * light);
                    byte g = (byte)(85 + 120 * light);
                    byte b = (byte)(130 + 110 * light);
                    using var fillPaint = new SKPaint { Color = new SKColor(r, g, b, 235), Style = SKPaintStyle.Fill, IsAntialias = true };
                    canvas.DrawPath(path, fillPaint);
                }

                canvas.DrawPath(path, wirePaint);
            }
        }

        const float cos30 = 0.8660254f;
        const float sin30 = 0.5f;

        RenderQuadrant(SKRect.Create(margin, margin, cellW, cellH), "VISTA FRONTALE (XY)",
            v => (v.X, -v.Y, v.Z), shaded: false);
        RenderQuadrant(SKRect.Create(margin + cellW + 16, margin, cellW, cellH), "VISTA LATERALE (ZY)",
            v => (v.Z, -v.Y, v.X), shaded: false);
        RenderQuadrant(SKRect.Create(margin, margin + cellH + 16, cellW, cellH), "VISTA SUPERIORE (XZ)",
            v => (v.X, v.Z, v.Y), shaded: false);
        RenderQuadrant(SKRect.Create(margin + cellW + 16, margin + cellH + 16, cellW, cellH), "VISTA 3D ISOMETRICA SHADED",
            v => ((v.X - v.Z) * cos30, -v.Y + (v.X + v.Z) * sin30 * 0.5f, v.X + v.Y + v.Z), shaded: true);

        var footerRect = SKRect.Create(margin, height - margin - footerH, availW, footerH);
        using var footerBg = new SKPaint { Color = new SKColor(22, 28, 44), Style = SKPaintStyle.Fill };
        canvas.DrawRect(footerRect, footerBg);
        canvas.DrawRect(footerRect, borderPaint);

        canvas.DrawText($"FLIP 3D BLUEPRINT — {fileName}", footerRect.Left + 20, footerRect.Top + 34, SKTextAlign.Left, titleFont, accentTextPaint);
        string stats = FormattableString.Invariant(
            $"Vertici Unici: {uniqueVerts.Count}   |   Triangoli: {faces.Count}   |   Bounding Box: {sizeX:F2} × {sizeY:F2} × {sizeZ:F2}");
        canvas.DrawText(stats, footerRect.Left + 20, footerRect.Top + 64, SKTextAlign.Left, metaFont, textPaint);
    }

    private static void RenderMeshBlueprintToImage(string srcPath, string srcExt, string dstPath, string dstExt, ConversionContext context)
    {
        var tris = LoadMesh(srcPath, srcExt);
        const int width = 1600, height = 1200;
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        DrawMeshBlueprintCanvas(canvas, width, height, Path.GetFileName(srcPath), tris);
        canvas.Flush();

        if (dstExt == "bmp")
        {
            ConversionEngine.WriteImage(bitmap, dstPath, "BMP");
            return;
        }

        var format = dstExt switch
        {
            "png" => SKEncodedImageFormat.Png,
            "webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg
        };
        using var data = bitmap.Encode(format, 92) ?? throw new IOException($"Encoder non disponibile: {dstExt}");
        using var fs = File.Create(dstPath);
        data.SaveTo(fs);
    }

    private static void RenderMeshBlueprintToPdf(string srcPath, string srcExt, string dstPath, ConversionContext context)
    {
        var tris = LoadMesh(srcPath, srcExt);
        const int width = 1600, height = 1200;
        using var stream = File.Create(dstPath);
        using var pdf = SKDocument.CreatePdf(stream) ?? throw new IOException("Encoder PDF non disponibile.");
        using var pageCanvas = pdf.BeginPage(width, height);
        DrawMeshBlueprintCanvas(pageCanvas, width, height, Path.GetFileName(srcPath), tris);
        pdf.EndPage();
        pdf.Close();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MESH → TURNTABLE 360° (rasterizzatore software con z-buffer, shaded + wireframe)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Dati di riepilogo del modello renderizzato dal turntable.</summary>
    internal readonly record struct TurntableInfo(int VertexCount, int TriangleCount, int FrameCount, int Size);

    private const float TurntableCameraDistance = 3.4f;
    private const float TurntablePitch = 0.33f; // ~19° dall'alto

    /// <summary>
    /// Renderizza <paramref name="frameCount"/> fotogrammi quadrati che ruotano il modello di 360° sull'asse verticale:
    /// luce studio lambertiana, griglia prospettica, wipe Shaded/Wireframe arancione e badge poligoni/vertici.
    /// Ogni fotogramma è consegnato come pixel 0xAARRGGBB a <paramref name="onFrame"/>.
    /// </summary>
    internal static TurntableInfo RenderTurntableFrames(
        string path, string? ext, int frameCount, int size, Action<int, int[]> onFrame,
        CancellationToken cancellationToken = default, string? upAxis = null)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        frameCount = Math.Clamp(frameCount, 2, 120);
        size = Math.Clamp(size, 160, 1080);

        string normalized = (string.IsNullOrEmpty(ext) ? Path.GetExtension(path) : ext).TrimStart('.').ToLowerInvariant();
        var tris = LoadMesh(path, normalized);
        if (tris.Count == 0) throw new IOException("Il modello 3D non contiene triangoli da renderizzare.");
        var (uniqueVerts, faces) = BuildIndexedMesh(tris);

        // I formati da stampa 3D/CAD usano Z verso l'alto; gli altri Y verso l'alto.
        bool zUp = upAxis is { Length: > 0 }
            ? upAxis.Equals("z", StringComparison.OrdinalIgnoreCase)
            : normalized is "stl" or "3mf";

        int triCount = tris.Count;
        var px = new float[triCount * 3];
        var py = new float[triCount * 3];
        var pz = new float[triCount * 3];
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (int t = 0; t < triCount; t++)
        {
            var tri = tris[t];
            for (int k = 0; k < 3; k++)
            {
                Vec3 v = k == 0 ? tri.A : k == 1 ? tri.B : tri.C;
                float x = v.X, y = zUp ? v.Z : v.Y, z = zUp ? -v.Y : v.Z;
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) x = y = z = 0;
                int i = t * 3 + k;
                px[i] = x; py[i] = y; pz[i] = z;
                if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
            }
        }

        float centerX = (minX + maxX) / 2f, centerY = (minY + maxY) / 2f, centerZ = (minZ + maxZ) / 2f;
        float radius = 1e-6f;
        for (int i = 0; i < px.Length; i++)
        {
            float dx = px[i] - centerX, dy = py[i] - centerY, dz = pz[i] - centerZ;
            float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d > radius) radius = d;
        }
        float unit = 1f / radius;
        for (int i = 0; i < px.Length; i++)
        {
            px[i] = (px[i] - centerX) * unit;
            py[i] = (py[i] - centerY) * unit;
            pz[i] = (pz[i] - centerZ) * unit;
        }
        float groundY = (minY - centerY) * unit;

        // Normali per faccia nello spazio modello.
        var nx = new float[triCount];
        var ny = new float[triCount];
        var nz = new float[triCount];
        for (int t = 0; t < triCount; t++)
        {
            int i = t * 3;
            float ux = px[i + 1] - px[i], uy = py[i + 1] - py[i], uz = pz[i + 1] - pz[i];
            float vx = px[i + 2] - px[i], vy = py[i + 2] - py[i], vz = pz[i + 2] - pz[i];
            float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            float length = MathF.Sqrt(cx * cx + cy * cy + cz * cz);
            if (length > 1e-12f) { nx[t] = cx / length; ny[t] = cy / length; nz[t] = cz / length; }
        }

        const int ss = 2; // supersampling 2x per l'anti-aliasing
        int w = size * ss;
        var depth = new float[w * w];
        var color = new int[w * w];
        var background = BuildTurntableBackground(w);
        var sx = new float[px.Length];
        var sy = new float[px.Length];
        var sInv = new float[px.Length];

        float focal = 0.66f * (w / 2f) * TurntableCameraDistance * 0.955f;
        float screenCx = w / 2f, screenCy = w * 0.475f;
        float cosP = MathF.Cos(TurntablePitch), sinP = MathF.Sin(TurntablePitch);

        string fileName = Path.GetFileName(path);
        var italian = CultureInfo.GetCultureInfo("it-IT");
        string badge = $"{faces.Count.ToString("N0", italian)} POLIGONI   •   {uniqueVerts.Count.ToString("N0", italian)} VERTICI";

        using var superBitmap = new SKBitmap(w, w, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var frameBitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var frameCanvas = new SKCanvas(frameBitmap);

        for (int frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float angle = MathF.Tau * frame / frameCount;
            float cosA = MathF.Cos(angle), sinA = MathF.Sin(angle);
            // Il wipe oscilla con la stessa fase della rotazione: il loop è perfettamente ciclico.
            float wipe = w * (0.5f + 0.17f * MathF.Sin(angle));

            Array.Copy(background, color, color.Length);
            Array.Clear(depth);

            (float X, float Y, float Inv) Project(float x, float y, float z)
            {
                float x1 = x * cosA + z * sinA;
                float z1 = -x * sinA + z * cosA;
                float y2 = y * cosP - z1 * sinP;
                float z2 = y * sinP + z1 * cosP;
                float inv = 1f / (TurntableCameraDistance - z2);
                return (screenCx + focal * x1 * inv, screenCy - focal * y2 * inv, inv);
            }

            for (int i = 0; i < px.Length; i++)
                (sx[i], sy[i], sInv[i]) = Project(px[i], py[i], pz[i]);

            // ── Facce: z-buffer + Lambert a tre luci (key, fill, top) ──
            for (int t = 0; t < triCount; t++)
            {
                int i = t * 3;
                float nx1 = nx[t] * cosA + nz[t] * sinA;
                float nz1 = -nx[t] * sinA + nz[t] * cosA;
                float ny2 = ny[t] * cosP - nz1 * sinP;
                float nz2 = ny[t] * sinP + nz1 * cosP;
                if (nz2 < 0) { nx1 = -nx1; ny2 = -ny2; nz2 = -nz2; }

                float key = MathF.Max(0, -0.500f * nx1 + 0.650f * ny2 + 0.572f * nz2);
                float fill = MathF.Max(0, 0.752f * nx1 + 0.100f * ny2 + 0.652f * nz2);
                float top = MathF.Max(0, 0.981f * ny2 - 0.196f * nz2);
                float light = MathF.Min(1f, 0.14f + 0.76f * key + 0.26f * fill + 0.16f * top);

                int shaded = TurntableRgb(40 + 198 * light, 44 + 196 * light, 58 + 188 * light);
                int hidden = TurntableRgb(21 + 12 * light, 23 + 13 * light, 30 + 16 * light);
                FillTurntableTriangle(depth, color, w, wipe, shaded, hidden,
                    sx[i], sy[i], sInv[i], sx[i + 1], sy[i + 1], sInv[i + 1], sx[i + 2], sy[i + 2], sInv[i + 2]);
            }

            // ── Griglia prospettica di base, occlusa dal modello ──
            const float gridExtent = 1.5f;
            const int gridLines = 7, gridSegments = 24;
            for (int line = -gridLines; line <= gridLines; line++)
            {
                float offset = gridExtent * line / gridLines;
                int gridColor = line == 0 ? 0x5A6278 : 0x3A4052;
                for (int axis = 0; axis < 2; axis++)
                {
                    for (int s = 0; s < gridSegments; s++)
                    {
                        float a = -gridExtent + 2 * gridExtent * s / gridSegments;
                        float b = -gridExtent + 2 * gridExtent * (s + 1) / gridSegments;
                        var p0 = axis == 0 ? Project(a, groundY, offset) : Project(offset, groundY, a);
                        var p1 = axis == 0 ? Project(b, groundY, offset) : Project(offset, groundY, b);
                        float fade0 = TurntableGridFade(MathF.Sqrt(a * a + offset * offset) / gridExtent);
                        float fade1 = TurntableGridFade(MathF.Sqrt(b * b + offset * offset) / gridExtent);
                        if (fade0 <= 0 && fade1 <= 0) continue;
                        DrawTurntableLine(depth, color, w, 0, gridColor, fade0, fade1, 1.0008f,
                            p0.X, p0.Y, p0.Inv, p1.X, p1.Y, p1.Inv);
                    }
                }
            }

            // ── Wireframe tecnico arancione FLIP con linee nascoste rimosse (solo lato destro del wipe) ──
            for (int t = 0; t < triCount; t++)
            {
                int i = t * 3;
                for (int e = 0; e < 3; e++)
                {
                    int a = i + e, b = i + (e + 1) % 3;
                    if (MathF.Max(sx[a], sx[b]) < wipe) continue;
                    DrawTurntableLine(depth, color, w, (int)MathF.Ceiling(wipe), 0xFF7A18, 0.95f, 0.95f, 1.004f,
                        sx[a], sy[a], sInv[a], sx[b], sy[b], sInv[b]);
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(color, 0, superBitmap.GetPixels(), color.Length);
            using (var image = SKImage.FromBitmap(superBitmap))
                frameCanvas.DrawImage(image, SKRect.Create(size, size), new SKSamplingOptions(SKFilterMode.Linear));
            DrawTurntableOverlay(frameCanvas, size, wipe / ss, fileName, badge);
            frameCanvas.Flush();

            var pixels = new int[size * size];
            System.Runtime.InteropServices.Marshal.Copy(frameBitmap.GetPixels(), pixels, 0, pixels.Length);
            onFrame(frame, pixels);
        }

        return new TurntableInfo(uniqueVerts.Count, faces.Count, frameCount, size);
    }

    private static int TurntableRgb(float r, float g, float b) =>
        unchecked((int)0xFF000000) |
        ((int)Math.Clamp(r, 0f, 255f) << 16) | ((int)Math.Clamp(g, 0f, 255f) << 8) | (int)Math.Clamp(b, 0f, 255f);

    private static float TurntableGridFade(float normalizedRadius)
    {
        float t = Math.Clamp((normalizedRadius - 0.45f) / 0.55f, 0f, 1f);
        return 0.85f * (1f - t * t * (3f - 2f * t));
    }

    /// <summary>Fondale scuro FLIP (#121318) con un alone morbido dietro al modello, a pochi livelli per restare compatto in GIF.</summary>
    private static int[] BuildTurntableBackground(int w)
    {
        var pixels = new int[w * w];
        float cx = w / 2f, cy = w * 0.46f, reach = w * 0.62f;
        for (int y = 0; y < w; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = (x - cx) / reach, dy = (y - cy) / reach;
                float glow = MathF.Max(0f, 1f - MathF.Sqrt(dx * dx + dy * dy));
                float level = MathF.Round(glow * glow * 6f) / 6f;
                pixels[y * w + x] = TurntableRgb(0x12 + 10 * level, 0x13 + 11 * level, 0x18 + 14 * level);
            }
        }
        return pixels;
    }

    private static void FillTurntableTriangle(
        float[] depth, int[] color, int w, float wipe, int shaded, int hidden,
        float x0, float y0, float i0, float x1, float y1, float i1, float x2, float y2, float i2)
    {
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-7f || !float.IsFinite(area)) return;

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))));
        int maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))));
        int maxY = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;

        float invArea = 1f / area;
        // Coordinate baricentriche incrementali valutate al centro del pixel.
        float startX = minX + 0.5f, startY = minY + 0.5f;
        float b0Row = ((x1 - startX) * (y2 - startY) - (x2 - startX) * (y1 - startY)) * invArea;
        float b1Row = ((x2 - startX) * (y0 - startY) - (x0 - startX) * (y2 - startY)) * invArea;
        float b0Dx = (y1 - y2) * invArea, b0Dy = (x2 - x1) * invArea;
        float b1Dx = (y2 - y0) * invArea, b1Dy = (x0 - x2) * invArea;
        const float edge = -1e-5f;

        for (int y = minY; y <= maxY; y++, b0Row += b0Dy, b1Row += b1Dy)
        {
            float b0 = b0Row, b1 = b1Row;
            int row = y * w;
            for (int x = minX; x <= maxX; x++, b0 += b0Dx, b1 += b1Dx)
            {
                float b2 = 1f - b0 - b1;
                if (b0 < edge || b1 < edge || b2 < edge) continue;
                float inv = b0 * i0 + b1 * i1 + b2 * i2;
                int index = row + x;
                if (inv <= depth[index]) continue;
                depth[index] = inv;
                color[index] = x < wipe ? shaded : hidden;
            }
        }
    }

    /// <summary>Linea spessa 2 px (1 px dopo il downsampling) con test di profondità e alpha interpolato.</summary>
    private static void DrawTurntableLine(
        float[] depth, int[] color, int w, int clipMinX, int rgb, float alpha0, float alpha1, float depthBias,
        float x0, float y0, float i0, float x1, float y1, float i1)
    {
        if (!float.IsFinite(x0 + y0 + x1 + y1)) return;
        if (MathF.Max(x0, x1) < clipMinX || MathF.Min(x0, x1) >= w || MathF.Max(y0, y1) < 0 || MathF.Min(y0, y1) >= w) return;

        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0)));
        if (steps <= 0) steps = 1;
        if (steps > w * 4) return;
        int lineR = (rgb >> 16) & 0xFF, lineG = (rgb >> 8) & 0xFF, lineB = rgb & 0xFF;

        for (int s = 0; s <= steps; s++)
        {
            float t = (float)s / steps;
            int x = (int)(x0 + (x1 - x0) * t), y = (int)(y0 + (y1 - y0) * t);
            float inv = (i0 + (i1 - i0) * t) * depthBias;
            float alpha = alpha0 + (alpha1 - alpha0) * t;
            if (alpha <= 0.01f) continue;

            for (int oy = 0; oy < 2; oy++)
            {
                int yy = y + oy;
                if (yy < 0 || yy >= w) continue;
                for (int ox = 0; ox < 2; ox++)
                {
                    int xx = x + ox;
                    if (xx < clipMinX || xx < 0 || xx >= w) continue;
                    int index = yy * w + xx;
                    if (inv < depth[index]) continue;
                    int under = color[index];
                    int r = (int)(((under >> 16) & 0xFF) * (1 - alpha) + lineR * alpha);
                    int g = (int)(((under >> 8) & 0xFF) * (1 - alpha) + lineG * alpha);
                    int b = (int)((under & 0xFF) * (1 - alpha) + lineB * alpha);
                    color[index] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                }
            }
        }
    }

    private static void DrawTurntableOverlay(SKCanvas canvas, int size, float wipeX, string fileName, string badge)
    {
        float k = size / 720f;
        var accent = new SKColor(0xFF, 0x7A, 0x18);
        using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var titleFont = new SKFont(bold, 19 * k);
        using var labelFont = new SKFont(bold, 11.5f * k);
        using var badgeFont = new SKFont(bold, 15 * k);
        using var accentPaint = new SKPaint { Color = accent, IsAntialias = true };
        using var textPaint = new SKPaint { Color = new SKColor(0xF3, 0xF4, 0xF6), IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = new SKColor(0x9C, 0xA3, 0xAF), IsAntialias = true };

        // Intestazione
        canvas.DrawText("FLIP", 28 * k, 44 * k, SKTextAlign.Left, titleFont, accentPaint);
        canvas.DrawText("TURNTABLE 360°", 28 * k + titleFont.MeasureText("FLIP ") , 44 * k, SKTextAlign.Left, titleFont, textPaint);
        string shownName = fileName.Length > 34 ? fileName[..31] + "…" : fileName;
        canvas.DrawText(shownName, size - 28 * k, 43 * k, SKTextAlign.Right, labelFont, mutedPaint);

        // Divisore del wipe con etichette Shaded / Wireframe
        float top = 70 * k, bottom = size - 92 * k;
        using var dividerPaint = new SKPaint { Color = accent.WithAlpha(215), StrokeWidth = 2 * k, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawLine(wipeX, top, wipeX, bottom, dividerPaint);
        canvas.DrawCircle(wipeX, top, 4 * k, accentPaint);
        canvas.DrawCircle(wipeX, bottom, 4 * k, accentPaint);
        canvas.DrawText("SHADED", wipeX - 12 * k, top + 4 * k, SKTextAlign.Right, labelFont, textPaint);
        canvas.DrawText("WIREFRAME", wipeX + 12 * k, top + 4 * k, SKTextAlign.Left, labelFont, accentPaint);

        // Badge inferiore: poligoni / vertici
        float badgeWidth = badgeFont.MeasureText(badge) + 64 * k, badgeHeight = 42 * k;
        var badgeRect = SKRect.Create((size - badgeWidth) / 2f, size - 28 * k - badgeHeight, badgeWidth, badgeHeight);
        using var badgeBg = new SKPaint { Color = new SKColor(0x1A, 0x1C, 0x23, 240), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var badgeBorder = new SKPaint { Color = new SKColor(0x2E, 0x32, 0x40), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * k, IsAntialias = true };
        canvas.DrawRoundRect(badgeRect, badgeHeight / 2f, badgeHeight / 2f, badgeBg);
        canvas.DrawRoundRect(badgeRect, badgeHeight / 2f, badgeHeight / 2f, badgeBorder);
        canvas.DrawCircle(badgeRect.Left + 22 * k, badgeRect.MidY, 4.5f * k, accentPaint);
        canvas.DrawText(badge, badgeRect.MidX + 8 * k, badgeRect.MidY + 5.5f * k, SKTextAlign.Center, badgeFont, textPaint);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SVG → DXF (Conversione Bidirezionale Vettoriale)
    // ══════════════════════════════════════════════════════════════════════════

    private static void ConvertSvgToDxf(string srcPath, string dstPath)
    {
        var doc = XDocument.Load(srcPath);
        var root = doc.Root ?? throw new IOException("SVG privo di elemento radice.");
        var sb = new StringBuilder();
        WriteDxfHeader(sb);
        int entitiesWritten = 0;

        foreach (var el in root.Descendants())
        {
            string kind = el.Name.LocalName;
            if (kind == "line")
            {
                float x1 = ParseSvgAttr(el, "x1"), y1 = ParseSvgAttr(el, "y1");
                float x2 = ParseSvgAttr(el, "x2"), y2 = ParseSvgAttr(el, "y2");
                WriteDxfLine(sb, x1, -y1, x2, -y2);
                entitiesWritten++;
            }
            else if (kind == "rect")
            {
                float x = ParseSvgAttr(el, "x"), y = ParseSvgAttr(el, "y");
                float w = ParseSvgAttr(el, "width"), h = ParseSvgAttr(el, "height");
                if (w > 0 && h > 0)
                {
                    WriteDxfLine(sb, x, -y, x + w, -y);
                    WriteDxfLine(sb, x + w, -y, x + w, -(y + h));
                    WriteDxfLine(sb, x + w, -(y + h), x, -(y + h));
                    WriteDxfLine(sb, x, -(y + h), x, -y);
                    entitiesWritten += 4;
                }
            }
            else if (kind == "circle")
            {
                float cx = ParseSvgAttr(el, "cx"), cy = ParseSvgAttr(el, "cy"), r = ParseSvgAttr(el, "r");
                if (r > 0)
                {
                    sb.AppendLine("0\nCIRCLE\n8\n0");
                    sb.AppendLine("10\n" + cx.ToString("F6", CultureInfo.InvariantCulture));
                    sb.AppendLine("20\n" + (-cy).ToString("F6", CultureInfo.InvariantCulture));
                    sb.AppendLine("40\n" + r.ToString("F6", CultureInfo.InvariantCulture));
                    entitiesWritten++;
                }
            }
            else if (kind is "polygon" or "polyline")
            {
                float[] pts = SvgNumbers((string?)el.Attribute("points") ?? "");
                for (int i = 0; i + 3 < pts.Length; i += 2)
                {
                    WriteDxfLine(sb, pts[i], -pts[i + 1], pts[i + 2], -pts[i + 3]);
                    entitiesWritten++;
                }
                if (kind == "polygon" && pts.Length >= 4)
                {
                    WriteDxfLine(sb, pts[^2], -pts[^1], pts[0], -pts[1]);
                    entitiesWritten++;
                }
            }
            else if (kind == "path")
            {
                string d = (string?)el.Attribute("d") ?? "";
                using var skPath = SKPath.ParseSvgPathData(d);
                if (skPath != null && skPath.PointCount >= 2)
                {
                    var pts = skPath.Points;
                    for (int i = 0; i < pts.Length - 1; i++)
                    {
                        if (pts[i] != pts[i + 1])
                        {
                            WriteDxfLine(sb, pts[i].X, -pts[i].Y, pts[i + 1].X, -pts[i + 1].Y);
                            entitiesWritten++;
                        }
                    }
                }
            }
        }

        if (entitiesWritten == 0)
        {
            WriteDxfLine(sb, 0, 0, 100, 100);
        }

        WriteDxfFooter(sb);
        File.WriteAllText(dstPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static float ParseSvgAttr(XElement el, string attrName)
    {
        string? val = (string?)el.Attribute(attrName);
        if (string.IsNullOrWhiteSpace(val)) return 0f;
        var m = NumberPattern.Match(val.Trim());
        return m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MESH → VECTOR (proiezione 2D ortografica)
    // ══════════════════════════════════════════════════════════════════════════

    private static void MeshToVector(string srcPath, string srcExt, string dstPath, string dstExt)
    {
        var tris = LoadMesh(srcPath, srcExt);
        if (tris.Count == 0) throw new IOException("Nessun triangolo da proiettare.");

        // Proiezione isometrica: X' = x - z*cos30, Y' = y - z*sin30
        const float cos30 = 0.866025f;
        const float sin30 = 0.5f;

        static (float px, float py) Project(Vec3 v)
            => (v.X - v.Z * cos30, -(v.Y - v.Z * sin30)); // Y invertito per coordinate schermo

        // Calcola bounding box proiettato
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var t in tris)
        {
            foreach (var v in new[] { t.A, t.B, t.C })
            {
                var (px, py) = Project(v);
                if (px < minX) minX = px;
                if (py < minY) minY = py;
                if (px > maxX) maxX = px;
                if (py > maxY) maxY = py;
            }
        }

        const float canvasSize = 800;
        float rangeX = maxX - minX, rangeY = maxY - minY;
        float scale = 720 / Math.Max(Math.Max(rangeX, rangeY), 1e-6f);
        float OffsetX(float px) => (canvasSize - rangeX * scale) / 2 + (px - minX) * scale;
        float OffsetY(float py) => (canvasSize - rangeY * scale) / 2 + (py - minY) * scale;

        if (dstExt == "svg")
        {
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{canvasSize}\" height=\"{canvasSize}\" viewBox=\"0 0 {canvasSize} {canvasSize}\">"));
            sb.AppendLine("<rect width=\"800\" height=\"800\" fill=\"#1a1a2e\"/>");
            sb.AppendLine("<g stroke=\"#4cc9f0\" stroke-width=\"0.5\" fill=\"none\" opacity=\"0.8\">");

            foreach (var t in tris)
            {
                var (ax, ay) = Project(t.A);
                var (bx, by) = Project(t.B);
                var (cx, cy) = Project(t.C);
                sb.AppendLine(FormattableString.Invariant(
                    $"<polygon points=\"{OffsetX(ax):F2},{OffsetY(ay):F2} {OffsetX(bx):F2},{OffsetY(by):F2} {OffsetX(cx):F2},{OffsetY(cy):F2}\"/>"));
            }

            sb.AppendLine("</g>");
            sb.AppendLine("</svg>");
            File.WriteAllText(dstPath, sb.ToString(), new UTF8Encoding(false));
        }
        else if (dstExt == "dxf")
        {
            var sb = new StringBuilder();
            WriteDxfHeader(sb);

            foreach (var t in tris)
            {
                var (ax, ay) = Project(t.A);
                var (bx, by) = Project(t.B);
                var (cx, cy) = Project(t.C);
                WriteDxfLine(sb, OffsetX(ax), canvasSize - OffsetY(ay), OffsetX(bx), canvasSize - OffsetY(by));
                WriteDxfLine(sb, OffsetX(bx), canvasSize - OffsetY(by), OffsetX(cx), canvasSize - OffsetY(cy));
                WriteDxfLine(sb, OffsetX(cx), canvasSize - OffsetY(cy), OffsetX(ax), canvasSize - OffsetY(ay));
            }

            WriteDxfFooter(sb);
            File.WriteAllText(dstPath, sb.ToString(), new UTF8Encoding(false));
        }
    }

    // ── DXF: proiezione XY delle entità 2D in spazio modello ────────────────

    private static XElement LoadDxfProjection(string path)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length % 2 != 0) throw new IOException("DXF non valido: coppia codice/valore incompleta.");
        var groups = new (int Code, string Value)[lines.Length / 2];
        for (int i = 0; i < groups.Length; i++)
        {
            if (!int.TryParse(lines[i * 2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                throw new IOException("DXF ASCII non valido o formato binario non supportato.");
            groups[i] = (code, lines[i * 2 + 1].Trim());
        }

        XNamespace ns = "http://www.w3.org/2000/svg";
        var drawing = new XElement(ns + "g", new XAttribute("transform", "scale(1 -1)"),
            new XAttribute("fill", "none"), new XAttribute("stroke", "#4cc9f0"));
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        void Include(float x, float y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        int start = Array.FindIndex(groups, pair => pair == (0, "SECTION"));
        while (start >= 0 && (start + 1 == groups.Length || groups[start + 1] != (2, "ENTITIES")))
            start = Array.FindIndex(groups, start + 1, pair => pair == (0, "SECTION"));
        if (start < 0) throw new IOException("DXF senza sezione ENTITIES.");
        int end = Array.FindIndex(groups, start + 2, pair => pair == (0, "ENDSEC"));
        if (end < 0) throw new IOException("Sezione ENTITIES incompleta.");

        for (int i = start + 2; i < end;)
        {
            var (entityCode, kind) = groups[i++];
            if (entityCode != 0) throw new IOException("Entità DXF non valida.");
            int first = i;
            while (i < end && groups[i].Code != 0) i++;
            var fields = groups[first..i];
            string? Field(int code) => Array.Find(fields, field => field.Code == code).Value;
            float Number(int code, float? fallback = null) => Field(code) is { } value
                ? ParseNumber(value)
                : fallback ?? throw new IOException($"DXF {kind}: manca il codice {code}.");

            if (Number(67, 0) == 1) continue;
            if (Number(210, 0) != 0 || Number(220, 0) != 0 || Number(230, 1) != 1 || Number(39, 0) != 0)
                throw new NotSupportedException($"DXF {kind}: estrusione o spessore fuori dal piano XY.");

            switch (kind)
            {
                case "LINE":
                    float x1 = Number(10), y1 = Number(20), x2 = Number(11), y2 = Number(21);
                    Include(x1, y1);
                    Include(x2, y2);
                    drawing.Add(new XElement(ns + "line", new XAttribute("x1", x1), new XAttribute("y1", y1),
                        new XAttribute("x2", x2), new XAttribute("y2", y2)));
                    break;
                case "CIRCLE" or "ARC":
                    float cx = Number(10), cy = Number(20), radius = Number(40);
                    if (radius <= 0) throw new IOException($"DXF {kind}: raggio non valido.");
                    if (kind == "CIRCLE")
                    {
                        Include(cx - radius, cy - radius);
                        Include(cx + radius, cy + radius);
                        drawing.Add(new XElement(ns + "circle", new XAttribute("cx", cx), new XAttribute("cy", cy),
                            new XAttribute("r", radius)));
                        break;
                    }

                    static float PositiveAngle(float angle) => (angle % 360 + 360) % 360;
                    (float X, float Y) PointAt(float degrees)
                    {
                        double radians = degrees * Math.PI / 180;
                        return (cx + radius * (float)Math.Cos(radians), cy + radius * (float)Math.Sin(radians));
                    }
                    float startAngle = PositiveAngle(Number(50)), endAngle = PositiveAngle(Number(51));
                    float sweep = PositiveAngle(endAngle - startAngle);
                    if (sweep == 0) throw new IOException("DXF ARC: angoli coincidenti.");
                    var from = PointAt(startAngle);
                    var to = PointAt(endAngle);
                    Include(from.X, from.Y);
                    Include(to.X, to.Y);
                    for (int cardinal = 0; cardinal < 360; cardinal += 90)
                    {
                        if (PositiveAngle(cardinal - startAngle) >= sweep) continue;
                        var point = PointAt(cardinal);
                        Include(point.X, point.Y);
                    }
                    drawing.Add(new XElement(ns + "path", new XAttribute("d", FormattableString.Invariant(
                        $"M {from.X:R} {from.Y:R} A {radius:R} {radius:R} 0 {(sweep > 180 ? 1 : 0)} 1 {to.X:R} {to.Y:R}"))));
                    break;
                case "LWPOLYLINE":
                    if (fields.Any(f => f.Code is 40 or 41 or 42 or 43 && ParseNumber(f.Value) != 0))
                        throw new NotSupportedException("DXF LWPOLYLINE: larghezza o archi non supportati.");
                    bool closed = ((int)Number(70, 0) & 1) != 0;
                    var points = new List<string>();
                    for (int j = 0; j < fields.Length; j++)
                    {
                        if (fields[j].Code != 10) continue;
                        float x = ParseNumber(fields[j].Value);
                        if (++j >= fields.Length || fields[j].Code != 20)
                            throw new IOException("DXF LWPOLYLINE: coordinata Y mancante.");
                        float y = ParseNumber(fields[j].Value);
                        Include(x, y);
                        points.Add(FormattableString.Invariant($"{x:R},{y:R}"));
                    }
                    if (points.Count < 2 || points.Count != Number(90))
                        throw new IOException("DXF LWPOLYLINE: numero di vertici non valido.");
                    drawing.Add(new XElement(ns + (closed ? "polygon" : "polyline"),
                        new XAttribute("points", string.Join(" ", points))));
                    break;
                default:
                    throw new NotSupportedException($"Entità DXF non supportata: {kind}.");
            }
        }

        if (!drawing.HasElements) throw new IOException("DXF senza geometria 2D in spazio modello.");
        float span = Math.Max(maxX - minX, maxY - minY);
        if (!float.IsFinite(span) || span <= 0) throw new IOException("Limiti DXF non validi.");
        float pad = span * 0.04f;
        float width = maxX - minX + 2 * pad, height = maxY - minY + 2 * pad;
        drawing.SetAttributeValue("stroke-width", span / 400);
        return new XElement(ns + "svg",
            new XAttribute("viewBox", FormattableString.Invariant($"{minX - pad:R} {-maxY - pad:R} {width:R} {height:R}")),
            drawing);
    }

    // ── SVG: sottoinsieme statico con riempimenti e contorni pieni ─────────

    private static readonly Regex NumberPattern = new(
        @"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
    private static readonly Regex TransformPattern = new(
        @"\G[\s,]*([a-zA-Z]+)\s*\(([^()]*)\)", RegexOptions.CultureInvariant);
    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";

    private static float ParseNumber(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number)
            ? number : throw new IOException($"Valore numerico non valido: {value}.");

    private static float[] SvgNumbers(string value)
    {
        var numbers = new List<float>();
        int position = 0;
        foreach (Match match in NumberPattern.Matches(value))
        {
            if (value[position..match.Index].Any(ch => !char.IsWhiteSpace(ch) && ch != ','))
                throw new IOException($"Elenco di numeri SVG non valido: {value}.");
            numbers.Add(ParseNumber(match.Value));
            position = match.Index + match.Length;
        }

        if (!string.IsNullOrWhiteSpace(value[position..]))
            throw new IOException($"Elenco di numeri SVG non valido: {value}.");
        return numbers.ToArray();
    }

    private static void ApplySvgTransform(SKCanvas canvas, string transform)
    {
        int position = 0;
        foreach (Match match in TransformPattern.Matches(transform))
        {
            float[] args = SvgNumbers(match.Groups[2].Value);
            SKMatrix matrix = (match.Groups[1].Value, args.Length) switch
            {
                ("matrix", 6) => new SKMatrix(args[0], args[2], args[4], args[1], args[3], args[5], 0, 0, 1),
                ("translate", 1 or 2) => SKMatrix.CreateTranslation(args[0], args.Length == 2 ? args[1] : 0),
                ("scale", 1 or 2) => SKMatrix.CreateScale(args[0], args.Length == 2 ? args[1] : args[0]),
                ("rotate", 1) => SKMatrix.CreateRotationDegrees(args[0]),
                ("rotate", 3) => SKMatrix.CreateRotationDegrees(args[0], args[1], args[2]),
                ("skewX", 1) => SKMatrix.CreateSkew(MathF.Tan(args[0] * MathF.PI / 180), 0),
                ("skewY", 1) => SKMatrix.CreateSkew(0, MathF.Tan(args[0] * MathF.PI / 180)),
                _ => throw new NotSupportedException($"Trasformazione SVG non supportata: {match.Value}.")
            };
            canvas.Concat(matrix);
            position = match.Index + match.Length;
        }

        if (!string.IsNullOrWhiteSpace(transform[position..]))
            throw new IOException($"Trasformazione SVG non valida: {transform}.");
    }

    private static float SvgLength(string value, float reference)
    {
        value = value.Trim();
        var number = NumberPattern.Match(value);
        if (!number.Success || number.Index != 0) throw new IOException($"Misura SVG non valida: {value}.");
        float factor = value[number.Length..] switch
        {
            "" or "px" => 1,
            "%" => reference / 100,
            "in" => 96,
            "cm" => 96 / 2.54f,
            "mm" => 96 / 25.4f,
            "pt" => 96 / 72f,
            "pc" => 16,
            _ => throw new NotSupportedException($"Unità SVG non supportata: {value}.")
        };
        return ParseNumber(number.Value) * factor;
    }

    private readonly record struct SvgStyle(
        SKColor? Fill, SKColor? Stroke, float StrokeWidth, float FillOpacity, float StrokeOpacity,
        SKPathFillType FillType, SKStrokeCap Cap, SKStrokeJoin Join, float Miter, bool Visible)
    {
        internal static SvgStyle Default => new(SKColors.Black, null, 1, 1, 1, SKPathFillType.Winding,
            SKStrokeCap.Butt, SKStrokeJoin.Miter, 4, true);
    }

    private static SKColor? SvgColor(string value)
    {
        if (value == "none") return null;
        if (value == "transparent") return SKColors.Transparent;
        if (value.StartsWith('#'))
        {
            if (value.Length == 5)
                value = "#" + string.Concat(value[1..].Select(ch => new string(ch, 2)));
            if (value.Length == 9) value = "#" + value[7..] + value.Substring(1, 6);
            if (SKColor.TryParse(value, out var hex)) return hex;
        }
        if (value.StartsWith("rgb(", StringComparison.Ordinal) && value.EndsWith(')') &&
            SvgNumbers(value[4..^1]) is [float red, float green, float blue])
            return new SKColor((byte)Math.Clamp(red, 0, 255), (byte)Math.Clamp(green, 0, 255), (byte)Math.Clamp(blue, 0, 255));
        var named = System.Drawing.Color.FromName(value);
        if (named.IsKnownColor && !named.IsSystemColor) return new SKColor(named.R, named.G, named.B, named.A);
        throw new NotSupportedException($"Colore o riempimento SVG non supportato: {value}.");
    }

    private static float SvgOpacity(string value) => Math.Clamp(SvgLength(value, 1), 0, 1);

    private static Dictionary<string, string> SvgAttributes(XElement element, string geometry)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        const string presentation = "fill stroke stroke-width fill-opacity stroke-opacity opacity fill-rule stroke-linecap stroke-linejoin stroke-miterlimit display visibility";
        string[] allowed = (geometry + " " + presentation + " transform").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration || attr.Name == XNamespace.Xml + "space") continue;
            string name = attr.Name.LocalName;
            if (attr.Name.Namespace == XNamespace.None && name is "id" or "version" or "style" or "class") continue;
            if (attr.Name.Namespace != XNamespace.None || !allowed.Contains(name, StringComparer.Ordinal))
                throw new NotSupportedException($"Attributo SVG non supportato: {attr.Name}.");
            values.Add(name, attr.Value.Trim());
        }

        foreach (string declaration in ((string?)element.Attribute("style") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = declaration.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !presentation.Split(' ').Contains(parts[0], StringComparer.Ordinal))
                throw new NotSupportedException($"Stile SVG non supportato: {declaration}.");
            values[parts[0]] = parts[1];
        }
        return values;
    }

    private static void DrawSvg(SKCanvas canvas, XElement element, float width, float height,
        SvgStyle inherited, CancellationToken ct, bool root = false)
    {
        ct.ThrowIfCancellationRequested();
        if (element.Name.Namespace != SvgNamespace && element.Name.Namespace != XNamespace.None)
            throw new NotSupportedException($"Elemento SVG non supportato: {element.Name}.");
        string kind = element.Name.LocalName;
        if (kind is "title" or "desc" or "metadata") return;
        string geometry = kind switch
        {
            "svg" when root => "width height viewBox preserveAspectRatio",
            "g" => "",
            "path" => "d",
            "rect" => "x y width height rx ry",
            "line" => "x1 y1 x2 y2",
            "circle" => "cx cy r",
            "ellipse" => "cx cy rx ry",
            "polygon" or "polyline" => "points",
            _ => throw new NotSupportedException($"Elemento SVG non supportato: {kind}.")
        };

        var attr = SvgAttributes(element, geometry);
        string? Get(string key) => attr.GetValueOrDefault(key);
        float Length(string key, float reference, float fallback = 0) =>
            Get(key) is { } value ? SvgLength(value, reference) : fallback;
        float diag = (float)(Math.Sqrt((double)width * width + (double)height * height) / Math.Sqrt(2));

        if (Get("display") == "none") return;
        if (Get("display") is not (null or "inline" or "block"))
            throw new NotSupportedException($"Display SVG non supportato: {Get("display")}.");

        var style = inherited with
        {
            Fill = Get("fill") is { } fill ? SvgColor(fill) : inherited.Fill,
            Stroke = Get("stroke") is { } stroke ? SvgColor(stroke) : inherited.Stroke,
            StrokeWidth = Length("stroke-width", diag, inherited.StrokeWidth),
            FillOpacity = Get("fill-opacity") is { } fo ? SvgOpacity(fo) : inherited.FillOpacity,
            StrokeOpacity = Get("stroke-opacity") is { } so ? SvgOpacity(so) : inherited.StrokeOpacity,
            FillType = Get("fill-rule") switch
            {
                null => inherited.FillType, "nonzero" => SKPathFillType.Winding, "evenodd" => SKPathFillType.EvenOdd,
                _ => throw new NotSupportedException("Regola di riempimento SVG non supportata.")
            },
            Cap = Get("stroke-linecap") switch
            {
                null => inherited.Cap, "butt" => SKStrokeCap.Butt, "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square,
                _ => throw new NotSupportedException("Estremità del tratto SVG non supportata.")
            },
            Join = Get("stroke-linejoin") switch
            {
                null => inherited.Join, "miter" => SKStrokeJoin.Miter, "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel,
                _ => throw new NotSupportedException("Giunzione del tratto SVG non supportata.")
            },
            Miter = Get("stroke-miterlimit") is { } limit ? ParseNumber(limit) : inherited.Miter,
            Visible = Get("visibility") switch
            {
                null => inherited.Visible, "visible" => true, "hidden" or "collapse" => false,
                _ => throw new NotSupportedException("Visibilità SVG non supportata.")
            }
        };
        if (style.StrokeWidth < 0 || style.Miter < 1)
            throw new IOException("Larghezza o limite di giunzione del tratto SVG non validi.");

        int saveCount = canvas.SaveCount;
        canvas.Save();
        try
        {
            if (!root && Get("transform") is { } transform) ApplySvgTransform(canvas, transform);
            if (Get("opacity") is { } opacity)
            {
                using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(SvgOpacity(opacity) * 255)) };
                canvas.SaveLayer(layer);
            }
            if (kind is "g" or "svg")
            {
                foreach (var child in element.Elements()) DrawSvg(canvas, child, width, height, style, ct);
                return;
            }
            if (!style.Visible) return;

            using SKPath path = kind == "path"
                ? SKPath.ParseSvgPathData(Get("d") ?? "") ?? throw new IOException("Tracciato SVG non valido.")
                : BuildSvgShape(kind, Length, Get("points"), width, height, diag);
            path.FillType = style.FillType;
            using var paint = new SKPaint { IsAntialias = true };
            if (kind != "line" && style.Fill is { } fillColor)
            {
                paint.Color = fillColor.WithAlpha((byte)Math.Round(fillColor.Alpha * style.FillOpacity));
                canvas.DrawPath(path, paint);
            }
            if (style.Stroke is { } strokeColor && style.StrokeWidth > 0)
            {
                paint.Color = strokeColor.WithAlpha((byte)Math.Round(strokeColor.Alpha * style.StrokeOpacity));
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = style.StrokeWidth;
                paint.StrokeCap = style.Cap;
                paint.StrokeJoin = style.Join;
                paint.StrokeMiter = style.Miter;
                canvas.DrawPath(path, paint);
            }
        }
        finally
        {
            canvas.RestoreToCount(saveCount);
        }
    }

    private static SKPath BuildSvgShape(string kind, Func<string, float, float, float> length, string? points,
        float width, float height, float diag)
    {
        using var builder = new SKPathBuilder();
        float X(string key) => length(key, width, 0);
        float Y(string key) => length(key, height, 0);
        switch (kind)
        {
            case "rect":
                float x = X("x"), y = Y("y"), w = X("width"), h = Y("height");
                float rx = length("rx", width, float.NaN), ry = length("ry", height, rx);
                if (float.IsNaN(rx)) rx = float.IsNaN(ry) ? 0 : ry;
                if (float.IsNaN(ry)) ry = rx;
                if (w < 0 || h < 0 || rx < 0 || ry < 0) throw new IOException("Rettangolo SVG non valido.");
                if (w > 0 && h > 0)
                    builder.AddRoundRect(SKRect.Create(x, y, w, h), Math.Min(rx, w / 2), Math.Min(ry, h / 2));
                break;
            case "circle" or "ellipse":
                float radiusX = kind == "circle" ? length("r", diag, 0) : X("rx");
                float radiusY = kind == "circle" ? radiusX : Y("ry");
                if (radiusX < 0 || radiusY < 0) throw new IOException("Raggio SVG non valido.");
                if (radiusX > 0 && radiusY > 0)
                    builder.AddOval(SKRect.Create(X("cx") - radiusX, Y("cy") - radiusY, 2 * radiusX, 2 * radiusY));
                break;
            case "line":
                builder.MoveTo(X("x1"), Y("y1"));
                builder.LineTo(X("x2"), Y("y2"));
                break;
            case "polygon" or "polyline":
                float[] coordinates = SvgNumbers(points ?? "");
                if (coordinates.Length % 2 != 0) throw new IOException("Coordinate SVG incomplete.");
                if (coordinates.Length == 0) break;
                builder.MoveTo(coordinates[0], coordinates[1]);
                for (int i = 2; i < coordinates.Length; i += 2)
                    builder.LineTo(coordinates[i], coordinates[i + 1]);
                if (kind == "polygon") builder.Close();
                break;
        }
        return builder.Detach();
    }

    private readonly record struct SvgViewport(XElement Root, float Width, float Height, SKRect ViewBox);

    private static SvgViewport LoadViewport(string path, string extension)
    {
        XElement root;
        if (extension == "dxf")
        {
            root = LoadDxfProjection(path);
        }
        else
        {
            var document = XDocument.Load(path);
            if (document.DescendantNodes().OfType<XProcessingInstruction>().Any(pi => pi.Target == "xml-stylesheet"))
                throw new NotSupportedException("I fogli di stile SVG esterni non sono supportati.");
            root = document.Root ?? throw new IOException("Elemento radice SVG assente.");
        }
        if (root.Name.LocalName != "svg") throw new IOException("Elemento radice SVG assente.");
        float[] box = SvgNumbers((string?)root.Attribute("viewBox") ?? "");
        if (box.Length != 0 && (box.Length != 4 || box[2] <= 0 || box[3] <= 0))
            throw new IOException("viewBox SVG non valido o vuoto.");
        float fallbackWidth = box.Length == 4 ? box[2] : 300;
        float fallbackHeight = box.Length == 4 ? box[3] : 150;
        string? widthValue = (string?)root.Attribute("width"), heightValue = (string?)root.Attribute("height");
        float width = widthValue != null ? SvgLength(widthValue, fallbackWidth) : fallbackWidth;
        float height = heightValue != null ? SvgLength(heightValue, fallbackHeight) : fallbackHeight;
        if (box.Length == 4 && widthValue == null && heightValue != null) width *= height / fallbackHeight;
        if (box.Length == 4 && heightValue == null && widthValue != null) height *= width / fallbackWidth;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new IOException("Dimensioni SVG non valide.");
        return new(root, width, height, box.Length == 4
            ? SKRect.Create(box[0], box[1], box[2], box[3]) : SKRect.Create(width, height));
    }

    private static void RenderViewport(SKCanvas canvas, SvgViewport viewport, CancellationToken ct)
    {
        var (root, width, height, box) = viewport;
        canvas.ClipRect(SKRect.Create(width, height));
        if ((string?)root.Attribute("transform") is { } transform) ApplySvgTransform(canvas, transform);
        string[] aspect = ((string?)root.Attribute("preserveAspectRatio") ?? "xMidYMid meet")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        float sx = width / box.Width, sy = height / box.Height;
        if (aspect is not ["none"])
        {
            if (aspect.Length is < 1 or > 2 || (aspect.Length == 2 && aspect[1] is not ("meet" or "slice")))
                throw new IOException("preserveAspectRatio SVG non valido.");
            string align = aspect[0];
            if (!Regex.IsMatch(align, @"\Ax(Min|Mid|Max)Y(Min|Mid|Max)\z"))
                throw new IOException("Allineamento SVG non valido.");
            sx = sy = aspect.Length == 2 && aspect[1] == "slice" ? Math.Max(sx, sy) : Math.Min(sx, sy);
            static float Align(string position) => position switch { "Min" => 0, "Mid" => 0.5f, _ => 1 };
            canvas.Translate((width - box.Width * sx) * Align(align.Substring(1, 3)),
                (height - box.Height * sy) * Align(align.Substring(5, 3)));
        }

        canvas.Scale(sx, sy);
        canvas.Translate(-box.Left, -box.Top);
        DrawSvg(canvas, root, box.Width, box.Height, SvgStyle.Default, ct, root: true);
    }

    private static void RenderVectorToImage(string source, string extension, string destination, string target, ConversionContext context)
    {
        var viewport = LoadViewport(source, extension);
        int size = context.CustomParameters.TryGetValue("size", out string? custom) && int.TryParse(custom, out int pixels)
            ? Math.Clamp(pixels, 100, 8000) : 1200;
        float scale = size / Math.Max(viewport.Width, viewport.Height);
        int width = Math.Max(1, (int)Math.Round(viewport.Width * scale));
        int height = Math.Max(1, (int)Math.Round(viewport.Height * scale));
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(target is "jpg" or "jpeg" or "bmp" ? SKColors.White : SKColors.Transparent);
        canvas.Scale(width / viewport.Width, height / viewport.Height);
        RenderViewport(canvas, viewport, context.CancellationToken);
        canvas.Flush();
        context.CancellationToken.ThrowIfCancellationRequested();
        if (target == "bmp")
        {
            string staged = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
                $"flip-bmp-{Guid.NewGuid():N}.tmp");
            try
            {
                ConversionEngine.WriteImage(bitmap, staged, "BMP");
                context.CancellationToken.ThrowIfCancellationRequested();
                File.Move(staged, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
            return;
        }
        var format = target switch
        {
            "png" => SKEncodedImageFormat.Png,
            "webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg
        };
        int quality = context.CustomParameters.TryGetValue("quality", out string? configured) && int.TryParse(configured, out int q)
            ? Math.Clamp(q, 1, 100) : 92;
        using var data = bitmap.Encode(format, quality) ?? throw new IOException($"Encoder non disponibile: {target}.");
        using var file = File.Create(destination);
        data.SaveTo(file);
    }

    private static void RenderVectorToPdf(string source, string extension, string destination, ConversionContext context)
    {
        var viewport = LoadViewport(source, extension);
        float scale = 595 / Math.Max(viewport.Width, viewport.Height);
        float width = Math.Max(1, viewport.Width * scale), height = Math.Max(1, viewport.Height * scale);
        using var recording = new SKPictureRecorder();
        var canvas = recording.BeginRecording(SKRect.Create(width, height));
        canvas.Scale(scale);
        RenderViewport(canvas, viewport, context.CancellationToken);
        using var picture = recording.EndRecording();
        context.CancellationToken.ThrowIfCancellationRequested();

        using var stream = File.Create(destination);
        using var pdf = SKDocument.CreatePdf(stream) ?? throw new IOException("Encoder PDF non disponibile.");
        pdf.BeginPage(width, height).DrawPicture(picture);
        pdf.EndPage();
        pdf.Close();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GEOSPATIAL (GPX ↔ KML ↔ GeoJSON)
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class GeoPoint(double lat, double lon, double? ele = null, string? name = null)
    {
        public double Lat { get; } = lat;
        public double Lon { get; } = lon;
        public double? Ele { get; } = ele;
        public string? Name { get; } = name;
    }

    private sealed class GeoTrack(string name, List<GeoPoint> points)
    {
        public string Name { get; } = name;
        public List<GeoPoint> Points { get; } = points;
    }

    private sealed class GeoData
    {
        public List<GeoPoint> Waypoints { get; } = [];
        public List<GeoTrack> Tracks { get; } = [];
    }

    // ── Loader GPX ─────────────────────────────────────────────────────────

    private static GeoData LoadGpx(string path)
    {
        var doc = XDocument.Load(path);
        XNamespace gpxNs = "http://www.topografix.com/GPX/1/1";
        // Prova anche senza namespace
        XNamespace empty = XNamespace.None;

        var data = new GeoData();

        foreach (var wpt in doc.Descendants(gpxNs + "wpt").Concat(doc.Descendants(empty + "wpt")))
        {
            double.TryParse(wpt.Attribute("lat")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat);
            double.TryParse(wpt.Attribute("lon")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon);
            double? ele = null;
            string? eleStr = wpt.Element(gpxNs + "ele")?.Value ?? wpt.Element(empty + "ele")?.Value;
            if (double.TryParse(eleStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double e)) ele = e;
            string? name = wpt.Element(gpxNs + "name")?.Value ?? wpt.Element(empty + "name")?.Value;
            data.Waypoints.Add(new GeoPoint(lat, lon, ele, name));
        }

        foreach (var trk in doc.Descendants(gpxNs + "trk").Concat(doc.Descendants(empty + "trk")))
        {
            string trkName = trk.Element(gpxNs + "name")?.Value ?? trk.Element(empty + "name")?.Value ?? "Track";
            var pts = new List<GeoPoint>();
            foreach (var trkpt in trk.Descendants(gpxNs + "trkpt").Concat(trk.Descendants(empty + "trkpt")))
            {
                double.TryParse(trkpt.Attribute("lat")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat);
                double.TryParse(trkpt.Attribute("lon")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon);
                double? ele = null;
                string? eleStr = trkpt.Element(gpxNs + "ele")?.Value ?? trkpt.Element(empty + "ele")?.Value;
                if (double.TryParse(eleStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double ev)) ele = ev;
                pts.Add(new GeoPoint(lat, lon, ele));
            }
            if (pts.Count > 0) data.Tracks.Add(new GeoTrack(trkName, pts));
        }

        if (data.Waypoints.Count == 0 && data.Tracks.Count == 0)
            throw new IOException("GPX: nessun waypoint o traccia trovata.");

        return data;
    }

    // ── Loader KML ─────────────────────────────────────────────────────────

    private static GeoData LoadKml(string path)
    {
        var doc = XDocument.Load(path);
        XNamespace kmlNs = "http://www.opengis.net/kml/2.2";
        XNamespace empty = XNamespace.None;

        var data = new GeoData();

        foreach (var pm in doc.Descendants(kmlNs + "Placemark").Concat(doc.Descendants(empty + "Placemark")))
        {
            string? name = pm.Element(kmlNs + "name")?.Value ?? pm.Element(empty + "name")?.Value;

            // Point
            var point = pm.Descendants(kmlNs + "Point").Concat(pm.Descendants(empty + "Point")).FirstOrDefault();
            if (point != null)
            {
                string? coords = point.Element(kmlNs + "coordinates")?.Value ?? point.Element(empty + "coordinates")?.Value;
                if (!string.IsNullOrWhiteSpace(coords))
                {
                    var pt = ParseKmlCoord(coords.Trim());
                    if (pt != null)
                        data.Waypoints.Add(new GeoPoint(pt.Value.lat, pt.Value.lon, pt.Value.ele, name));
                }
            }

            // LineString (tracciato)
            var lineString = pm.Descendants(kmlNs + "LineString").Concat(pm.Descendants(empty + "LineString")).FirstOrDefault();
            if (lineString != null)
            {
                string? coords = lineString.Element(kmlNs + "coordinates")?.Value ?? lineString.Element(empty + "coordinates")?.Value;
                if (!string.IsNullOrWhiteSpace(coords))
                {
                    var pts = ParseKmlCoords(coords);
                    if (pts.Count > 0)
                        data.Tracks.Add(new GeoTrack(name ?? "Track", pts));
                }
            }
        }

        if (data.Waypoints.Count == 0 && data.Tracks.Count == 0)
            throw new IOException("KML: nessun placemark con coordinate trovato.");

        return data;
    }

    private static (double lat, double lon, double? ele)? ParseKmlCoord(string coord)
    {
        string[] parts = coord.Split(',');
        if (parts.Length < 2) return null;
        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) return null;
        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) return null;
        double? ele = null;
        if (parts.Length >= 3 && double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double e))
            ele = e;
        return (lat, lon, ele);
    }

    private static List<GeoPoint> ParseKmlCoords(string coordString)
    {
        var result = new List<GeoPoint>();
        string[] pairs = coordString.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string pair in pairs)
        {
            var parsed = ParseKmlCoord(pair.Trim());
            if (parsed.HasValue)
                result.Add(new GeoPoint(parsed.Value.lat, parsed.Value.lon, parsed.Value.ele));
        }
        return result;
    }

    // ── Loader GeoJSON ─────────────────────────────────────────────────────

    private static GeoData LoadGeoJson(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = new GeoData();

        System.Text.Json.JsonElement features;
        if (root.TryGetProperty("features", out features) && features.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("geometry", out var geom)) continue;
                string geoType = geom.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                string? name = feature.TryGetProperty("properties", out var props) && props.TryGetProperty("name", out var n)
                    ? n.GetString() : null;

                if (!geom.TryGetProperty("coordinates", out var coords)) continue;

                if (geoType == "Point" && coords.ValueKind == System.Text.Json.JsonValueKind.Array && coords.GetArrayLength() >= 2)
                {
                    double lon = coords[0].GetDouble();
                    double lat = coords[1].GetDouble();
                    double? ele = coords.GetArrayLength() >= 3 ? coords[2].GetDouble() : null;
                    data.Waypoints.Add(new GeoPoint(lat, lon, ele, name));
                }
                else if (geoType == "LineString" && coords.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var pts = new List<GeoPoint>();
                    foreach (var c in coords.EnumerateArray())
                    {
                        if (c.GetArrayLength() >= 2)
                        {
                            double lon = c[0].GetDouble();
                            double lat = c[1].GetDouble();
                            double? ele = c.GetArrayLength() >= 3 ? c[2].GetDouble() : null;
                            pts.Add(new GeoPoint(lat, lon, ele));
                        }
                    }
                    if (pts.Count > 0) data.Tracks.Add(new GeoTrack(name ?? "Track", pts));
                }
            }
        }

        if (data.Waypoints.Count == 0 && data.Tracks.Count == 0)
            throw new IOException("GeoJSON: nessuna feature Point o LineString trovata.");

        return data;
    }

    // ── Writer GPX ─────────────────────────────────────────────────────────

    private static void WriteGpx(GeoData data, string path)
    {
        XNamespace gpx = "http://www.topografix.com/GPX/1/1";
        var root = new XElement(gpx + "gpx",
            new XAttribute("version", "1.1"),
            new XAttribute("creator", "FLIP"),
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

        foreach (var wpt in data.Waypoints)
        {
            var el = new XElement(gpx + "wpt",
                new XAttribute("lat", wpt.Lat.ToString("F7", CultureInfo.InvariantCulture)),
                new XAttribute("lon", wpt.Lon.ToString("F7", CultureInfo.InvariantCulture)));
            if (wpt.Ele.HasValue)
                el.Add(new XElement(gpx + "ele", wpt.Ele.Value.ToString("F2", CultureInfo.InvariantCulture)));
            if (!string.IsNullOrEmpty(wpt.Name))
                el.Add(new XElement(gpx + "name", wpt.Name));
            root.Add(el);
        }

        foreach (var trk in data.Tracks)
        {
            var trkEl = new XElement(gpx + "trk",
                new XElement(gpx + "name", trk.Name));
            var trkseg = new XElement(gpx + "trkseg");
            foreach (var pt in trk.Points)
            {
                var trkpt = new XElement(gpx + "trkpt",
                    new XAttribute("lat", pt.Lat.ToString("F7", CultureInfo.InvariantCulture)),
                    new XAttribute("lon", pt.Lon.ToString("F7", CultureInfo.InvariantCulture)));
                if (pt.Ele.HasValue)
                    trkpt.Add(new XElement(gpx + "ele", pt.Ele.Value.ToString("F2", CultureInfo.InvariantCulture)));
                trkseg.Add(trkpt);
            }
            trkEl.Add(trkseg);
            root.Add(trkEl);
        }

        var xdoc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        xdoc.Save(fs);
        fs.Flush(true);
    }

    // ── Writer KML ─────────────────────────────────────────────────────────

    private static void WriteKml(GeoData data, string path)
    {
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        var doc = new XElement(kml + "kml",
            new XElement(kml + "Document",
                new XElement(kml + "name", "FLIP Export")));
        var document = doc.Element(kml + "Document")!;

        foreach (var wpt in data.Waypoints)
        {
            string coord = wpt.Ele.HasValue
                ? FormattableString.Invariant($"{wpt.Lon:F7},{wpt.Lat:F7},{wpt.Ele.Value:F2}")
                : FormattableString.Invariant($"{wpt.Lon:F7},{wpt.Lat:F7},0");

            document.Add(new XElement(kml + "Placemark",
                new XElement(kml + "name", wpt.Name ?? "Waypoint"),
                new XElement(kml + "Point",
                    new XElement(kml + "coordinates", coord))));
        }

        foreach (var trk in data.Tracks)
        {
            string coordStr = string.Join("\n",
                trk.Points.Select(p => p.Ele.HasValue
                    ? FormattableString.Invariant($"{p.Lon:F7},{p.Lat:F7},{p.Ele.Value:F2}")
                    : FormattableString.Invariant($"{p.Lon:F7},{p.Lat:F7},0")));

            document.Add(new XElement(kml + "Placemark",
                new XElement(kml + "name", trk.Name),
                new XElement(kml + "LineString",
                    new XElement(kml + "coordinates", coordStr))));
        }

        var xdoc = new XDocument(new XDeclaration("1.0", "utf-8", null), doc);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        xdoc.Save(fs);
        fs.Flush(true);
    }

    // ── Writer GeoJSON ─────────────────────────────────────────────────────

    private static void WriteGeoJson(GeoData data, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"type\": \"FeatureCollection\",");
        sb.AppendLine("  \"features\": [");

        var features = new List<string>();

        foreach (var wpt in data.Waypoints)
        {
            string coords = wpt.Ele.HasValue
                ? FormattableString.Invariant($"[{wpt.Lon:F7}, {wpt.Lat:F7}, {wpt.Ele.Value:F2}]")
                : FormattableString.Invariant($"[{wpt.Lon:F7}, {wpt.Lat:F7}]");
            string name = System.Text.Json.JsonSerializer.Serialize(wpt.Name ?? "Waypoint");
            features.Add(
                $"    {{\"type\": \"Feature\", \"properties\": {{\"name\": {name}}}, " +
                $"\"geometry\": {{\"type\": \"Point\", \"coordinates\": {coords}}}}}");
        }

        foreach (var trk in data.Tracks)
        {
            string coordArray = "[" + string.Join(", ",
                trk.Points.Select(p => p.Ele.HasValue
                    ? FormattableString.Invariant($"[{p.Lon:F7}, {p.Lat:F7}, {p.Ele.Value:F2}]")
                    : FormattableString.Invariant($"[{p.Lon:F7}, {p.Lat:F7}]"))) + "]";
            string name = System.Text.Json.JsonSerializer.Serialize(trk.Name);
            features.Add(
                $"    {{\"type\": \"Feature\", \"properties\": {{\"name\": {name}}}, " +
                $"\"geometry\": {{\"type\": \"LineString\", \"coordinates\": {coordArray}}}}}");
        }

        sb.AppendLine(string.Join(",\n", features));
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ── Router Geo ─────────────────────────────────────────────────────────

    private static void ConvertGeo(string srcPath, string srcExt, string dstPath, string dstExt)
    {
        GeoData data = srcExt switch
        {
            "gpx" => LoadGpx(srcPath),
            "kml" => LoadKml(srcPath),
            "geojson" => LoadGeoJson(srcPath),
            _ => throw new IOException($"Formato geospatiale sorgente non supportato: {srcExt}")
        };

        switch (dstExt)
        {
            case "gpx": WriteGpx(data, dstPath); break;
            case "kml": WriteKml(data, dstPath); break;
            case "geojson": WriteGeoJson(data, dstPath); break;
            default: throw new IOException($"Formato geospatiale destinazione non supportato: {dstExt}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  HELPER DXF
    // ══════════════════════════════════════════════════════════════════════════

    private static void WriteDxfHeader(StringBuilder sb)
    {
        sb.AppendLine("0");
        sb.AppendLine("SECTION");
        sb.AppendLine("2");
        sb.AppendLine("ENTITIES");
    }

    private static void WriteDxfFooter(StringBuilder sb)
    {
        sb.AppendLine("0");
        sb.AppendLine("ENDSEC");
        sb.AppendLine("0");
        sb.AppendLine("EOF");
    }

    private static void WriteDxfLine(StringBuilder sb, float x1, float y1, float x2, float y2)
    {
        sb.AppendLine("0");
        sb.AppendLine("LINE");
        sb.AppendLine("8");
        sb.AppendLine("0");
        sb.AppendLine("10");
        sb.AppendLine(x1.ToString("F6", CultureInfo.InvariantCulture));
        sb.AppendLine("20");
        sb.AppendLine(y1.ToString("F6", CultureInfo.InvariantCulture));
        sb.AppendLine("11");
        sb.AppendLine(x2.ToString("F6", CultureInfo.InvariantCulture));
        sb.AppendLine("21");
        sb.AppendLine(y2.ToString("F6", CultureInfo.InvariantCulture));
    }

}

