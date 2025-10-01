using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.IO;
using System.Reflection;

partial class Program
{
    static int gridWidth = 256;
    static int gridHeight = 256;

    static int quadVAO, quadVBO;
    static int updateShaderProgram, blitShaderProgram;
    static int stateTextureFront, stateTextureBack;
    static int framebuffer;

    static bool paused = false;
    static double stepInterval = 0.1;
    static double timeAccumulator = 0.0;

    static long stepCounter;
    static long stepCounterSpeed = 0;
    static double lastPrintTime = 0;
    static double lastSpeed = 0;
    static double timeSincePrint = 0;
    static Vector2? lastMousePos = null;
    static int brushSize = 1;

    const int MaxTypes = 16;
    static int ruleTex;
    static byte[] ruleData = new byte[MaxTypes * 9 * 2];
    static int interactionTex;
    static byte[] interactionData = new byte[MaxTypes * MaxTypes];
    static int typeColorTex;
    static byte[] typeColors = new byte[MaxTypes * 4];

    static int currentBrushType = 0;

    static int usedTypes = 0;

    static void Main()
    {
        var settings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(900, 900),
            Title = "Game of Life (OpenTK + GLSL)"
        };

        using var window = new GameWindow(GameWindowSettings.Default, settings);

        window.Load += () =>
        {
            // --- Quad ---
            float[] quad =
            {
                -1f, -1f, 0f, 0f,
                 1f, -1f, 1f, 0f,
                 1f,  1f, 1f, 1f,
                -1f, -1f, 0f, 0f,
                 1f,  1f, 1f, 1f,
                -1f,  1f, 0f, 1f
            };

            quadVAO = GL.GenVertexArray();
            quadVBO = GL.GenBuffer();
            GL.BindVertexArray(quadVAO);
            GL.BindBuffer(BufferTarget.ArrayBuffer, quadVBO);
            GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), quad, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
            GL.EnableVertexAttribArray(1);

            // --- Shaders ---
            int vs = CompileShader(LoadEmbedded("Shaders.fullscreen.vert"), ShaderType.VertexShader);
            int fsUpdate = CompileShader(LoadEmbedded("Shaders.gol_update.frag"), ShaderType.FragmentShader);
            int fsBlit = CompileShader(LoadEmbedded("Shaders.blit.frag"), ShaderType.FragmentShader);

            updateShaderProgram = LinkProgram(vs, fsUpdate);
            blitShaderProgram = LinkProgram(vs, fsBlit);

            GL.DeleteShader(vs);
            GL.DeleteShader(fsUpdate);
            GL.DeleteShader(fsBlit);

            // --- Rules ---
            InitRuleTexture();
            SetRule(0, [3], [2, 3]);       // Conway (классика)
            SetRule(1, [3], [2, 4]);    // HighLife (вариант Conway, рождается и при 6)
            UploadRules();

            // --- Colors ---
            InitTypeColorTexture();
            SetTypeColor(0, 255, 255, 255); // Conway = белый
            SetTypeColor(1, 255, 255, 0);   // HighLife = жёлтый
            UploadTypeColors();

            // --- Interactions ---
            InitInteractionTexture();
            // Conway видит себя и чуть-чуть HighLife
            SetInteraction(0, 0, 1.0f);
            //SetInteraction(0, 1, 0.5f);

            // HighLife видит Conway и сам себя
            SetInteraction(1, 1, 1.0f);
            //SetInteraction(1, 1, 1.0f);

            UploadInteractions();



            currentBrushType = 0;

            // --- Bind samplers ---
            GL.UseProgram(updateShaderProgram);
            GL.Uniform1(GL.GetUniformLocation(updateShaderProgram, "currentState"), 0);
            GL.Uniform1(GL.GetUniformLocation(updateShaderProgram, "ruleTex"), 1);
            GL.Uniform1(GL.GetUniformLocation(updateShaderProgram, "interactionTex"), 2);

            GL.UseProgram(blitShaderProgram);
            GL.Uniform1(GL.GetUniformLocation(blitShaderProgram, "currentState"), 0);
            GL.Uniform1(GL.GetUniformLocation(blitShaderProgram, "typeColors"), 1);

            // --- Textures ---
            ResetGameField(randomInit: true);

            framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, stateTextureBack, 0);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new Exception("FBO incomplete");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            GL.Viewport(0, 0, window.ClientSize.X, window.ClientSize.Y);
        };

        window.UpdateFrame += args =>
        {
            var kb = window.KeyboardState;

            if (kb.IsKeyPressed(Keys.C))
                ResetGameField(false);

            if (kb.IsKeyPressed(Keys.Space))
            {
                timeAccumulator = 0.0;
                paused = !paused;
            }

            if (kb.IsKeyPressed(Keys.Up))
                stepInterval = Math.Max(0.0005, stepInterval * 0.8); // быстрее

            if (kb.IsKeyPressed(Keys.Down))
                stepInterval = Math.Min(10.0, stepInterval * 1.25);  // медленнее

            if (kb.IsKeyPressed(Keys.Enter))
                DoSimulationStep();

            if (kb.IsKeyPressed(Keys.LeftBracket))  // [
                brushSize = Math.Max(0, brushSize - 1);
            if (kb.IsKeyPressed(Keys.RightBracket)) // ]
                brushSize = Math.Min(1000, brushSize + 1);


            if (kb.IsKeyPressed(Keys.Equal) || kb.IsKeyPressed(Keys.KeyPadAdd))
            {
                int newW = Math.Max(32, gridWidth / 2);
                int newH = Math.Max(32, gridHeight / 2);
                ResizeGameField(newW, newH);
            }

            if (kb.IsKeyPressed(Keys.Minus) || kb.IsKeyPressed(Keys.KeyPadSubtract))
            {
                int newW = Math.Min(4096, gridWidth * 2);
                int newH = Math.Min(4096, gridHeight * 2);
                ResizeGameField(newW, newH);
            }

            if (kb.IsKeyPressed(Keys.R))
                FillRandomShapes();

            // выбор типа кисти цифрами 1..N
            for (int i = 0; i < usedTypes; i++)
            {
                var key = Keys.D1 + i; // Keys.D1 .. Keys.D9
                if (kb.IsKeyPressed(key))
                {
                    currentBrushType = i;
                    Console.WriteLine($"Brush type set to {i}");
                }
            }
        };


        window.MouseDown += args => HandleDrawing(window, window.MouseState.Position);
        window.MouseMove += args => HandleDrawing(window, window.MouseState.Position);

        window.RenderFrame += args =>
        {
            timeAccumulator += args.Time;
            if (!paused)
            {
                while (timeAccumulator >= stepInterval)
                {
                    DoSimulationStep();
                    timeAccumulator -= stepInterval;
                }
            }

            timeSincePrint += args.Time;
            if (timeSincePrint >= 0.2)
            {
                PrintInfo();
                timeSincePrint = 0;
            }

            BlitToScreen(window);
            window.SwapBuffers();
        };

        window.Run();
    }
    static void ResizeGameField(int newW, int newH)
    {
        // создаём новые пустые текстуры (RGBA8)
        int newFront = CreateStateTexture(newW, newH, false);
        int newBack = CreateStateTexture(newW, newH, false);

        ApplyTextureParams(newFront);
        ApplyTextureParams(newBack);

        if (stateTextureFront != 0)
        {
            // читаем всю старую текстуру (RGBA)
            byte[] oldData = new byte[gridWidth * gridHeight * 4];
            GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.GetTexImage(TextureTarget.Texture2D, 0,
                           PixelFormat.Rgba, PixelType.UnsignedByte, oldData);

            int copyW = Math.Min(gridWidth, newW);
            int copyH = Math.Min(gridHeight, newH);

            // создаём буфер для нового блока
            byte[] copyData = new byte[copyW * copyH * 4];
            for (int y = 0; y < copyH; y++)
            {
                Array.Copy(
                    oldData, y * gridWidth * 4,
                    copyData, y * copyW * 4,
                    copyW * 4
                );
            }

            // вставляем в центр новой текстуры
            GL.BindTexture(TextureTarget.Texture2D, newFront);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0,
                             (newW - copyW) / 2, (newH - copyH) / 2,
                             copyW, copyH,
                             PixelFormat.Rgba, PixelType.UnsignedByte, copyData);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        if (stateTextureFront != 0) GL.DeleteTexture(stateTextureFront);
        if (stateTextureBack != 0) GL.DeleteTexture(stateTextureBack);

        stateTextureFront = newFront;
        stateTextureBack = newBack;
        gridWidth = newW;
        gridHeight = newH;

        PrintInfo();
    }


    static void FillRandomShapes()
    {
        var rnd = new Random();
        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);

        int shapeCount = rnd.Next(10, 30);
        shapeCount = 1;
        for (int i = 0; i < shapeCount; i++)
        {
            int w = rnd.Next(5, gridWidth / 4);
            int h = rnd.Next(5, gridHeight / 4);
            int cx = rnd.Next(0, gridWidth - w);
            int cy = rnd.Next(0, gridHeight - h);

            bool circle = rnd.Next(2) == 0;

            // случайный тип клетки: 0..2
            byte type = (byte)rnd.Next(1,2);
            
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int px = cx + x, py = cy + y;
                    if (px < 0 || py < 0 || px >= gridWidth || py >= gridHeight) continue;

                    bool inside = true;
                    if (circle)
                    {
                        float dx = x - w / 2f, dy = y - h / 2f;
                        inside = dx * dx + dy * dy <= (Math.Min(w, h) / 2f) * (Math.Min(w, h) / 2f);
                    }

                    if (inside)
                    {
                        byte[] rgba = { 255, type, 0, 255 }; // alive=255, type=G, A=255
                        GL.TexSubImage2D(TextureTarget.Texture2D, 0, px, py, 1, 1,
                                         PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
                    }
                }
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        Console.WriteLine($"Random shapes placed: {shapeCount}");
    }


    // === RULES ===
    static void InitRuleTexture()
    {
        ruleTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, ruleTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg8,
            9, MaxTypes, 0, PixelFormat.Rg, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
    }

    static void SetRule(int type, int[] birth, int[] survival)
    {
        for (int n = 0; n < 9; n++)
        {
            bool b = Array.IndexOf(birth, n) >= 0;
            bool s = Array.IndexOf(survival, n) >= 0;
            int baseIdx = (type * 9 + n) * 2;
            ruleData[baseIdx + 0] = (byte)(b ? 255 : 0);
            ruleData[baseIdx + 1] = (byte)(s ? 255 : 0);
        }
    }

    static void UploadRules()
    {
        GL.BindTexture(TextureTarget.Texture2D, ruleTex);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 9, MaxTypes,
            PixelFormat.Rg, PixelType.UnsignedByte, ruleData);
    }

    // === COLORS ===
    static void InitTypeColorTexture()
    {
        typeColorTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, typeColorTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            MaxTypes, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    }

    static void SetTypeColor(int type, byte r, byte g, byte b)
    {
        int i = type * 4;
        typeColors[i + 0] = r;
        typeColors[i + 1] = g;
        typeColors[i + 2] = b;
        typeColors[i + 3] = 255; // alpha

        if (type + 1 > usedTypes)
            usedTypes = type + 1; // обновляем максимум
    }

    static void UploadTypeColors()
    {
        GL.BindTexture(TextureTarget.Texture2D, typeColorTex);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, MaxTypes, 1,
            PixelFormat.Rgba, PixelType.UnsignedByte, typeColors);
    }

    // === INTERACTIONS ===
    static void InitInteractionTexture()
    {
        interactionTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, interactionTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8,
            MaxTypes, MaxTypes, 0, PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    }

    static void SetInteraction(int myType, int neighType, float weight01)
    {
        int index = myType * MaxTypes + neighType;
        interactionData[index] = (byte)(Math.Clamp(weight01, 0f, 1f) * 255);
    }

    static void UploadInteractions()
    {
        GL.BindTexture(TextureTarget.Texture2D, interactionTex);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, MaxTypes, MaxTypes,
            PixelFormat.Red, PixelType.UnsignedByte, interactionData);
    }

    // === SIMULATION ===
    static void DoSimulationStep()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, stateTextureBack, 0);

        GL.Viewport(0, 0, gridWidth, gridHeight);

        GL.UseProgram(updateShaderProgram);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, ruleTex);

        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, interactionTex);

        int locTexel = GL.GetUniformLocation(updateShaderProgram, "texelSize");
        GL.Uniform2(locTexel, 1.0f / gridWidth, 1.0f / gridHeight);

        GL.BindVertexArray(quadVAO);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        stepCounter++;

        (stateTextureFront, stateTextureBack) = (stateTextureBack, stateTextureFront);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    static void BlitToScreen(GameWindow window)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.Viewport(0, 0, window.ClientSize.X, window.ClientSize.Y);

        GL.ClearColor(0.05f, 0.05f, 0.07f, 1.0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.UseProgram(blitShaderProgram);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);

        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, typeColorTex);

        GL.BindVertexArray(quadVAO);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    // === TEXTURES ===
    static void ResetGameField(bool randomInit)
    {
        if (stateTextureFront != 0) GL.DeleteTexture(stateTextureFront);
        if (stateTextureBack != 0) GL.DeleteTexture(stateTextureBack);

        stateTextureFront = CreateStateTexture(gridWidth, gridHeight, randomInit);
        stateTextureBack = CreateStateTexture(gridWidth, gridHeight, false);

        ApplyTextureParams(stateTextureFront);
        ApplyTextureParams(stateTextureBack);
    }

    static int CreateStateTexture(int w, int h, bool randomInit)
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

        byte[] data = new byte[w * h * 4];
        if (randomInit)
        {
            var rnd = new Random();
            for (int i = 0; i < w * h; i++)
            {
                bool alive = rnd.Next(2) == 0;
                byte type = (byte)rnd.Next(Math.Max(1, usedTypes)); // если не задали — хотя бы 1
                data[i * 4 + 0] = (byte)(alive ? 255 : 0);
                data[i * 4 + 1] = type;
                data[i * 4 + 2] = 0;
                data[i * 4 + 3] = 255;
            }
        }

        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h,
            PixelFormat.Rgba, PixelType.UnsignedByte, data);

        return tex;
    }


    static void ApplyTextureParams(int tex)
    {
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    // === DRAWING ===
    static void HandleDrawing(GameWindow window, Vector2 mousePos)
    {
        bool left = window.MouseState.IsButtonDown(MouseButton.Left);
        bool right = window.MouseState.IsButtonDown(MouseButton.Right);

        if ((left || right) && lastMousePos.HasValue)
            DrawLineCells(window, lastMousePos.Value, mousePos, left, brushSize);
        else if (left || right)
        {
            int cx = (int)(mousePos.X / window.ClientSize.X * gridWidth);
            int cy = (int)((1.0f - mousePos.Y / window.ClientSize.Y) * gridHeight);
            SetCellWithBrush(cx, cy, left, brushSize);
        }

        if (left || right) lastMousePos = mousePos;
        else lastMousePos = null;
    }

    static void DrawLineCells(GameWindow window, Vector2 from, Vector2 to, bool alive, int brushSize)
    {
        int x0 = (int)(from.X / window.ClientSize.X * gridWidth);
        int y0 = (int)((1.0f - from.Y / window.ClientSize.Y) * gridHeight);
        int x1 = (int)(to.X / window.ClientSize.X * gridWidth);
        int y1 = (int)((1.0f - to.Y / window.ClientSize.Y) * gridHeight);

        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && y0 >= 0 && x0 < gridWidth && y0 < gridHeight)
                SetCellWithBrush(x0, y0, alive, brushSize);

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    static void SetCellWithBrush(int cx, int cy, bool alive, int brushSize)
    {
        byte a = (byte)(alive ? 255 : 0);
        byte t = (byte)currentBrushType;

        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        for (int dx = -brushSize; dx <= brushSize; dx++)
            for (int dy = -brushSize; dy <= brushSize; dy++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= gridWidth || y >= gridHeight) continue;
                byte[] px = { a, t, 0, 255 };
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, 1, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, px);
            }
    }

    // === INFO ===
    static void PrintInfo()
    {
        double now = DateTime.Now.Ticks / (double)TimeSpan.TicksPerSecond;
        double dt = now - lastPrintTime;

        if (dt >= 0.2)
        {
            lastSpeed = stepCounterSpeed / dt;
            stepCounterSpeed = 0;
            lastPrintTime = now;
        }

        Console.SetCursorPosition(0, 0);
        for (int i = 0; i < 5; i++)
            Console.WriteLine(new string(' ', Console.WindowWidth));
        Console.SetCursorPosition(0, 0);

        Console.WriteLine($"Step: {stepCounter} | Speed: {lastSpeed:F1} steps/sec | Field: {gridWidth}x{gridHeight}");
        Console.WriteLine($"Brush size: {brushSize}");
        Console.WriteLine($"Paused: {(paused ? "Yes" : "No")}");
        Console.WriteLine();
    }

    // === UTILS ===
    static int CompileShader(string source, ShaderType type)
    {
        int id = GL.CreateShader(type);
        GL.ShaderSource(id, EnsureTrailingNewline(source));
        GL.CompileShader(id);
        GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"Shader compile error [{type}]:\n{GL.GetShaderInfoLog(id)}");
        return id;
    }

    static int LinkProgram(int vert, int frag)
    {
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vert);
        GL.AttachShader(prog, frag);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new Exception($"Program link error:\n{GL.GetProgramInfoLog(prog)}");
        return prog;
    }

    static string LoadEmbedded(string relative)
    {
        var asm = Assembly.GetExecutingAssembly();
        string baseNs = asm.GetName().Name ?? "";
        string resName = baseNs + "." + relative.Replace('/', '.').Replace('\\', '.');

        using Stream? s = asm.GetManifestResourceStream(resName);
        if (s == null)
            throw new FileNotFoundException($"Embedded resource '{resName}' not found.\n" +
                $"Available:\n{string.Join("\n", asm.GetManifestResourceNames())}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static string EnsureTrailingNewline(string text)
        => text.EndsWith("\n") ? text : text + "\n";
}
