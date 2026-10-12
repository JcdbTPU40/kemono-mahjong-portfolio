#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ランキングパネル用のスプライトを手続き的に生成する。
/// 木枠・金/銀プレート・王冠（金銀銅）・番号タグ・ネジを PNG として書き出し、
/// Sprite としてインポート設定まで済ませる。
///
/// 手描き素材を持ち込まずコードで作るのは、
///   - 色・寸法をここ1か所で調整でき、再実行すれば全部そろって作り直せる
///   - 9スライスの border をコード側と食い違わせない
///   - Quest 向けに小さく保てる（全部で数百KB）
/// ため。形は符号付き距離（SDF）で塗るのでフチもアンチエイリアスもきれいに出る。
/// </summary>
public static class BuildRankingPanelArt
{
    public const string ArtDir = "Assets/Mahjong/Textures/Ranking";

    public const string FramePath = ArtDir + "/rank_frame.png";
    public const string PlateTopPath = ArtDir + "/rank_plate_gold.png";
    public const string PlateNormalPath = ArtDir + "/rank_plate_silver.png";
    public const string Crown1Path = ArtDir + "/rank_crown_gold.png";
    public const string Crown2Path = ArtDir + "/rank_crown_silver.png";
    public const string Crown3Path = ArtDir + "/rank_crown_bronze.png";
    public const string TagPath = ArtDir + "/rank_tag_blue.png";
    public const string ScrewGoldPath = ArtDir + "/rank_screw_gold.png";
    public const string ScrewSilverPath = ArtDir + "/rank_screw_silver.png";

    // 段位の紋章は BuildNameplates / BuildResultScreen / WireMainMenu と同じ実素材を使う。
    // ここだけ別の絵を生成すると、ネームプレート・終局画面・ランキングで紋章が食い違う。
    // 並びは 初獣→天獣（PlayerStatsStore.GetMajorRankIndexForLv の 0..5）。
    // Sprite ではなく Texture2D（textureType=Default）＝表示側は RawImage。
    public static readonly string[] RankEmblemPaths =
    {
        "Assets/Mahjong/Textures/UI/rank/初獣.png",
        "Assets/Mahjong/Textures/UI/rank/鋭爪.png",
        "Assets/Mahjong/Textures/UI/rank/剛牙.png",
        "Assets/Mahjong/Textures/UI/rank/獣威.png",
        "Assets/Mahjong/Textures/UI/rank/幻獣.png",
        "Assets/Mahjong/Textures/UI/rank/天獣.png",
    };

    // 9スライスの border（左, 下, 右, 上）
    public static readonly Vector4 FrameBorder = new Vector4(48f, 48f, 48f, 48f);
    public static readonly Vector4 PlateBorder = new Vector4(14f, 14f, 14f, 14f);

    [MenuItem("Tools/Kemono Mahjong/Build Ranking Panel Art")]
    public static void Run()
    {
        Directory.CreateDirectory(ArtDir);

        WriteFrame();
        WritePlate(PlateTopPath,
            new Color32(0xF2, 0xE4, 0xBC, 0xFF), new Color32(0xE7, 0xD3, 0x9E, 0xFF),
            new Color32(0xD2, 0xB8, 0x7C, 0xFF), new Color32(0xA5, 0x88, 0x4E, 0xFF));
        WritePlate(PlateNormalPath,
            new Color32(0xFA, 0xFA, 0xFA, 0xFF), new Color32(0xE8, 0xE8, 0xEA, 0xFF),
            new Color32(0xCB, 0xCB, 0xD0, 0xFF), new Color32(0x9C, 0x9C, 0xA2, 0xFF));

        WriteCrown(Crown1Path, new Color32(0xFF, 0xDD, 0x66, 0xFF), new Color32(0xB8, 0x7E, 0x0E, 0xFF),
            new Color32(0x4A, 0x2E, 0x06, 0xFF));
        WriteCrown(Crown2Path, new Color32(0xF2, 0xF2, 0xF6, 0xFF), new Color32(0x99, 0x9C, 0xA4, 0xFF),
            new Color32(0x3B, 0x3E, 0x45, 0xFF));
        WriteCrown(Crown3Path, new Color32(0xF0, 0xA5, 0x5E, 0xFF), new Color32(0xB0, 0x51, 0x14, 0xFF),
            new Color32(0x4B, 0x21, 0x06, 0xFF));

        WriteTag(TagPath, new Color32(0x7C, 0xC2, 0xE0, 0xFF), new Color32(0x2E, 0x6B, 0x8E, 0xFF),
            new Color32(0x18, 0x3E, 0x55, 0xFF));

        WriteScrew(ScrewGoldPath, new Color32(0xE2, 0xC8, 0x7E, 0xFF), new Color32(0x8A, 0x6F, 0x2A, 0xFF));
        WriteScrew(ScrewSilverPath, new Color32(0xDE, 0xDE, 0xE2, 0xFF), new Color32(0x87, 0x87, 0x8E, 0xFF));

        // 段位の紋章は実素材（Textures/UI/rank）を使うので、ここでは生成しない。

        AssetDatabase.Refresh();
        Debug.Log("[BuildRankingPanelArt] 完了: " + ArtDir + " に9枚のスプライトを生成しました。");
    }

    public static bool ArtExists()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(FramePath) == null
            || AssetDatabase.LoadAssetAtPath<Sprite>(PlateTopPath) == null
            || AssetDatabase.LoadAssetAtPath<Sprite>(TagPath) == null)
        {
            return false;
        }
        for (int i = 0; i < RankEmblemPaths.Length; ++i)
        {
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(RankEmblemPaths[i]) == null) { return false; }
        }
        return true;
    }

    // ============================================================
    // 木枠（9スライス・中央は透明）
    // ============================================================

    private static void WriteFrame()
    {
        const int size = 192;
        const int border = 48;
        var tex = NewTexture(size, size);

        Color32 edgeDark = new Color32(0x24, 0x0B, 0x0B, 0xFF);   // 外周と内周の締め
        Color32 bevelHi = new Color32(0x86, 0x33, 0x33, 0xFF);    // 面取りの明るい側
        Color32 wood = new Color32(0x50, 0x1B, 0x1B, 0xFF);       // 木の地
        Color32 woodDark = new Color32(0x3A, 0x12, 0x12, 0xFF);

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                // 端からの距離。9スライスでも角がそのまま使えるよう4辺の最小を見る。
                int d = Mathf.Min(Mathf.Min(x, size - 1 - x), Mathf.Min(y, size - 1 - y));
                if (d >= border)
                {
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    continue;
                }

                Color c;
                if (d < 4) { c = edgeDark; }
                else if (d < 13) { c = bevelHi; }
                else if (d < 40)
                {
                    // ごく薄い木目。決定的な式だけで作る（Randomは使わない）。
                    float grain = 0.5f + 0.5f * Mathf.Sin((x * 0.9f + y * 0.35f) * 0.7f);
                    c = Color.Lerp(woodDark, wood, 0.55f + 0.45f * grain);
                }
                else if (d < 45) { c = bevelHi; }
                else { c = edgeDark; }

                tex.SetPixel(x, y, c);
            }
        }

        Save(tex, FramePath, FrameBorder);
    }

    // ============================================================
    // プレート（9スライス・角丸＋上下の面取り）
    // ============================================================

    private static void WritePlate(string path, Color32 top, Color32 mid, Color32 bottom, Color32 edge)
    {
        const int w = 256;
        const int h = 96;
        const float radius = 9f;
        var tex = NewTexture(w, h);

        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                float d = RoundedRectDistance(x + 0.5f, y + 0.5f, 1.5f, 1.5f, w - 1.5f, h - 1.5f, radius);
                float coverage = Mathf.Clamp01(0.5f - d);
                if (coverage <= 0f)
                {
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    continue;
                }

                float t = (float)y / (h - 1);
                Color fill = t > 0.5f
                    ? Color.Lerp(mid, top, (t - 0.5f) * 2f)
                    : Color.Lerp(bottom, mid, t * 2f);

                // 上端のハイライトと下端の影で厚みを出す。
                if (y >= h - 5) { fill = Color.Lerp(fill, Color.white, 0.45f); }
                else if (y <= 4) { fill = Color.Lerp(fill, (Color)edge, 0.55f); }

                // 外周1.5pxは締め色。
                float edgeMix = Mathf.Clamp01((d + 2.0f) / 2.0f);
                fill = Color.Lerp(fill, edge, edgeMix * 0.9f);

                fill.a = coverage;
                tex.SetPixel(x, y, fill);
            }
        }

        Save(tex, path, PlateBorder);
    }

    // ============================================================
    // 王冠（3つの山＋玉＋台座。フチ付き）
    // ============================================================

    private static void WriteCrown(string path, Color32 top, Color32 bottom, Color32 outline)
    {
        const int size = 96;
        var tex = NewTexture(size, size);

        Vector2[] body =
        {
            new Vector2(15f, 33f), new Vector2(17f, 74f), new Vector2(34f, 43f),
            new Vector2(48f, 82f), new Vector2(62f, 43f), new Vector2(79f, 74f),
            new Vector2(81f, 33f),
        };
        Vector2[] ballCenters = { new Vector2(17f, 76f), new Vector2(48f, 84f), new Vector2(79f, 76f) };
        const float ballRadius = 6.5f;

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;

                float d = PolygonDistance(px, py, body);
                for (int i = 0; i < ballCenters.Length; ++i)
                {
                    float dc = Vector2.Distance(new Vector2(px, py), ballCenters[i]) - ballRadius;
                    if (dc < d) { d = dc; }
                }
                // 台座（帯）
                float band = RoundedRectDistance(px, py, 12f, 12f, 84f, 34f, 4f);
                if (band < d) { d = band; }

                float coverage = Mathf.Clamp01(0.5f - d);
                if (coverage <= 0f)
                {
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    continue;
                }

                float t = Mathf.Clamp01((py - 12f) / 74f);
                Color fill = Color.Lerp(bottom, top, t);
                // 帯の上に一段暗い影を入れて立体に見せる。
                if (py > 34f && py < 40f) { fill = Color.Lerp(fill, (Color)outline, 0.35f); }

                float outlineMix = Mathf.Clamp01((d + 3.0f) / 2.5f);
                fill = Color.Lerp(fill, outline, outlineMix);
                fill.a = coverage;
                tex.SetPixel(x, y, fill);
            }
        }

        Save(tex, path, Vector4.zero);
    }

    // ============================================================
    // 番号タグ（左が尖った五角形）
    // ============================================================

    private static void WriteTag(string path, Color32 top, Color32 bottom, Color32 outline)
    {
        const int size = 96;
        var tex = NewTexture(size, size);

        Vector2[] tag =
        {
            new Vector2(6f, 48f), new Vector2(26f, 76f), new Vector2(90f, 76f),
            new Vector2(90f, 20f), new Vector2(26f, 20f),
        };

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                float d = PolygonDistance(px, py, tag);

                float coverage = Mathf.Clamp01(0.5f - d);
                if (coverage <= 0f)
                {
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    continue;
                }

                float t = Mathf.Clamp01((py - 20f) / 56f);
                Color fill = Color.Lerp(bottom, top, t);
                if (py > 66f) { fill = Color.Lerp(fill, Color.white, 0.25f); }

                float outlineMix = Mathf.Clamp01((d + 3.0f) / 2.5f);
                fill = Color.Lerp(fill, outline, outlineMix);
                fill.a = coverage;
                tex.SetPixel(x, y, fill);
            }
        }

        Save(tex, path, Vector4.zero);
    }

    // ============================================================
    // ネジ
    // ============================================================

    private static void WriteScrew(string path, Color32 highlight, Color32 shade)
    {
        const int size = 32;
        var tex = NewTexture(size, size);
        Vector2 center = new Vector2(16f, 16f);
        const float radius = 11f;

        for (int y = 0; y < size; ++y)
        {
            for (int x = 0; x < size; ++x)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float d = Vector2.Distance(p, center) - radius;
                float coverage = Mathf.Clamp01(0.5f - d);
                if (coverage <= 0f)
                {
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    continue;
                }

                // 左上から光が当たっているように見せる。
                float lit = Mathf.Clamp01(0.5f + (p.y - center.y - (p.x - center.x)) / (radius * 2.4f));
                Color fill = Color.Lerp(shade, highlight, lit);
                float rim = Mathf.Clamp01((d + 2.5f) / 2.0f);
                fill = Color.Lerp(fill, Color.Lerp(shade, Color.black, 0.45f), rim);
                fill.a = coverage;
                tex.SetPixel(x, y, fill);
            }
        }

        Save(tex, path, Vector4.zero);
    }

    // ============================================================
    // 形状（符号付き距離。負なら内側）
    // ============================================================

    private static float PolygonDistance(float px, float py, Vector2[] poly)
    {
        var p = new Vector2(px, py);
        float best = float.MaxValue;
        bool inside = false;

        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[j];

            Vector2 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            float t = lengthSq > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq) : 0f;
            float dist = Vector2.Distance(p, a + ab * t);
            if (dist < best) { best = dist; }

            // 偶奇規則で内外を判定する。
            if (((a.y > p.y) != (b.y > p.y)) &&
                (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x))
            {
                inside = !inside;
            }
        }

        return inside ? -best : best;
    }

    private static float RoundedRectDistance(float px, float py, float minX, float minY,
                                             float maxX, float maxY, float radius)
    {
        float cx = (minX + maxX) * 0.5f;
        float cy = (minY + maxY) * 0.5f;
        float hx = (maxX - minX) * 0.5f - radius;
        float hy = (maxY - minY) * 0.5f - radius;
        float dx = Mathf.Abs(px - cx) - hx;
        float dy = Mathf.Abs(py - cy) - hy;
        float outside = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude;
        return outside + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;
    }

    // ============================================================
    // 書き出し
    // ============================================================

    private static Texture2D NewTexture(int w, int h)
    {
        return new Texture2D(w, h, TextureFormat.RGBA32, false);
    }

    private static void Save(Texture2D tex, string path, Vector4 border)
    {
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) { return; }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        // UIの細い線とグラデーションは圧縮ノイズが目立つ。全部合わせても数百KBなので非圧縮で持つ。
        importer.textureCompression = TextureImporterCompression.Uncompressed;

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        settings.spriteBorder = border;
        settings.spriteAlignment = (int)SpriteAlignment.Center;
        settings.spritePixelsPerUnit = 100f;
        importer.SetTextureSettings(settings);

        importer.SaveAndReimport();
    }
}
#endif
