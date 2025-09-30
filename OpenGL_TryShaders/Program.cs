using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System;
using System.IO;
using System.Reflection;

partial class Program
{
    // --- размеры поля
    static int gridWidth = 256;
    static int gridHeight = 256;

    // --- GL объекты
    static int quadVAO = 0, quadVBO = 0;
    static int updateShaderProgram = 0, blitShaderProgram = 0;
    static int stateTextureFront = 0, stateTextureBack = 0; // ping-pong
    static int framebuffer = 0;

    // --- управление
    static bool paused = false;
    static double stepInterval = 0.1;   // шаг каждые 0.1с = ~10 шаг/сек
    static double timeAccumulator = 0.0;

    static long stepCounter { get; set; }
    static long stepCounterSpeed = 0;
    static double lastPrintTime = 0;
    static double lastSpeed = 0;
    static Vector2? lastMousePos = null;
    static int brushSize = 1; // радиус кисти (1 => 3x3)
    static double timeSincePrint = 0;

    const double maxFps = 240;
    long frameCounter = 0;

    static DateTime lastInfoTime = DateTime.Now;
    static void Main()
    {
        var settings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(900, 900),
            Title = "Game of Life (OpenTK + GLSL, ping-pong)"
        };

        using var window = new GameWindow(GameWindowSettings.Default, settings);

        window.Load += () =>
        {
            // ---------- Fullscreen quad ----------
            float[] quad =
            {
                //   pos        uv
                -1f, -1f,     0f, 0f,
                 1f, -1f,     1f, 0f,
                 1f,  1f,     1f, 1f,

                -1f, -1f,     0f, 0f,
                 1f,  1f,     1f, 1f,
                -1f,  1f,     0f, 1f
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

            // ---------- Shaders ----------
            int vs = CompileShader(LoadEmbedded("Shaders.fullscreen.vert"), ShaderType.VertexShader);
            int fsUpdate = CompileShader(LoadEmbedded("Shaders.gol_update.frag"), ShaderType.FragmentShader);
            int fsBlit = CompileShader(LoadEmbedded("Shaders.blit.frag"), ShaderType.FragmentShader);

            updateShaderProgram = LinkProgram(vs, fsUpdate);
            blitShaderProgram = LinkProgram(vs, fsBlit);

            GL.DeleteShader(vs);
            GL.DeleteShader(fsUpdate);
            GL.DeleteShader(fsBlit);

            // ---------- Textures ----------
            ResetGameField(randomInit: true);

            // ---------- FBO (после создания текстур!) ----------
            framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                    TextureTarget.Texture2D, stateTextureBack, 0);
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
                throw new Exception("FBO incomplete: " + status);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // viewport под окно
            GL.Viewport(0, 0, window.ClientSize.X, window.ClientSize.Y);
        };

        // ----- клавиатура: очистка/пауза/скорость
        window.UpdateFrame += args =>
        {
            var kb = window.KeyboardState;

            if (kb.IsKeyPressed(Keys.C))
            {
                ResetGameField(false); // полная очистка (всё нули)
            }
            if (kb.IsKeyPressed(Keys.Space))
            {
                timeAccumulator = 0.0; // ⚡ сбрасываем, чтобы не "догоняло"
                paused = !paused;
            }


            if (kb.IsKeyPressed(Keys.Up))
            {
                stepInterval = Math.Max(0.001, stepInterval * 0.8); // быстрее (уменьшаем интервал)
                window.Title = $"Game of Life — speed: {1.0 / stepInterval:F1} steps/sec";
            }

            if (kb.IsKeyPressed(Keys.Down))
            {
                stepInterval = Math.Min(10.0, stepInterval * 1.25); // медленнее (увеличиваем интервал)
                window.Title = $"Game of Life — speed: {1.0 / stepInterval:F1} steps/sec";
            }


            if (kb.IsKeyPressed(Keys.Equal) || kb.IsKeyPressed(Keys.KeyPadAdd))
            {
                int newW = Math.Max(32, gridWidth / 2);
                int newH = Math.Max(32, gridHeight / 2);
                ResizeGameField(newW, newH);
            }

            if (kb.IsKeyPressed(Keys.Minus) || kb.IsKeyPressed(Keys.KeyPadSubtract))
            {
                int newW = Math.Min(2048, gridWidth * 2);
                int newH = Math.Min(2048, gridHeight * 2);
                ResizeGameField(newW, newH);
            }

            if (kb.IsKeyPressed(Keys.Enter))
            {
                DoSimulationStep();
                Console.WriteLine($"Manual step {AddSteps()}");
            }

            if (kb.IsKeyPressed(Keys.LeftBracket)) // [
            {
                brushSize = Math.Max(0, brushSize - 1);
                Console.WriteLine($"Brush size: {brushSize}");
            }

            if (kb.IsKeyPressed(Keys.RightBracket)) // ]
            {
                brushSize = Math.Min(100, brushSize + 1);
                Console.WriteLine($"Brush size: {brushSize}");
            }

            if (kb.IsKeyPressed(Keys.R))
            {
                FillRandomShapes();
            }


        };

        // ----- мышь: рисование (ЛКМ — поставить, ПКМ — стереть)
        window.MouseDown += args =>
        {
            HandleDrawing(window, window.MouseState.Position);
        };

        window.MouseMove += args =>
        {
            HandleDrawing(window, window.MouseState.Position);
        };


        // ----- рендер + апдейт (fixed timestep + ping-pong)
        window.RenderFrame += args =>
        {
            timeAccumulator += args.Time;

            if (!paused)
            {
                while (timeAccumulator >= stepInterval)
                {
                    DoSimulationStep(); // один шаг "Жизни"
                    timeAccumulator -= stepInterval;
                }
            }

            timeSincePrint += args.Time;
            if (timeSincePrint >= 0.2) // раз в секунду
            {
                PrintInfo();
                timeSincePrint = 0;
            }


            BlitToScreen(window);

            window.SwapBuffers(); // важно!
        };

        window.Run();

        // ---- cleanup (опционально)
        GL.DeleteVertexArray(quadVAO);
        GL.DeleteBuffer(quadVBO);
        GL.DeleteTexture(stateTextureFront);
        GL.DeleteTexture(stateTextureBack);
        GL.DeleteProgram(updateShaderProgram);
        GL.DeleteProgram(blitShaderProgram);
        GL.DeleteFramebuffer(framebuffer);
    }

    // ===== helpers =====

    //static void PrintStep(long steps, double stepsPerSec)
    //{
    //    int top = Console.CursorTop;   // запомним текущую строку
    //    Console.SetCursorPosition(0, 0);
    //    Console.Write($"Step: {steps} | Speed: {stepsPerSec:F1} steps/sec    ");
    //    Console.SetCursorPosition(0, top); // вернём курсор назад
    //}

    static void PrintInfo()
    {
        double now = DateTime.Now.Ticks / (double)TimeSpan.TicksPerSecond;
        double dt = now - lastPrintTime;

        if (dt >= 0.2) // обновляем раз в 0.2 сек
        {
            lastSpeed = stepCounterSpeed / dt;
            stepCounterSpeed = 0;
            lastPrintTime = now;
        }

        // всегда пишем с первой строки
        Console.SetCursorPosition(0, 0);

        // очищаем блок (например, 6 строк, чтобы стереть старое)
        for (int i = 0; i < 6; i++)
        {
            Console.WriteLine(new string(' ', Console.WindowWidth));
        }
        Console.SetCursorPosition(0, 0);

        // печатаем блок инфы
        Console.WriteLine($"Step: {stepCounter} | Speed: {lastSpeed:F1} steps/sec | Field: {gridWidth}x{gridHeight}");
        Console.WriteLine($"Brush: {brushSize}");
        Console.WriteLine($"Paused: {(paused ? "Yes" : "No")}");
        Console.WriteLine(); // пустая строка-разделитель
    }



    static long AddSteps(long count = 1)
    {
        stepCounter += count;
        stepCounterSpeed += count;
        return stepCounter;
    }
    static void FillRandomShapes()
    {
        // очистим поле
        //GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
        //byte[] empty = new byte[gridWidth * gridHeight];
        //GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, gridWidth, gridHeight,
        //                 PixelFormat.Red, PixelType.UnsignedByte, empty);

        Random rnd = new Random();

        int shapeCount = rnd.Next(10, 30); // количество фигур
        for (int i = 0; i < shapeCount; i++)
        {
            int w = rnd.Next(5, gridWidth/shapeCount);  // ширина фигуры
            int h = rnd.Next(5, gridHeight/shapeCount + (gridHeight/2 - rnd.Next(0, gridHeight/2)));  // высота фигуры
            int cx = rnd.Next(0, gridWidth - w);
            int cy = rnd.Next(0, gridHeight - h);

            // выбираем тип фигуры: 0 = прямоугольник, 1 = круг
            int type = rnd.Next(2);

            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int px = cx + x;
                    int py = cy + y;

                    bool inside = true;
                    if (type == 1) // круг
                    {
                        float dx = x - w / 2f;
                        float dy = y - h / 2f;
                        inside = (dx * dx + dy * dy <= (Math.Min(w, h) / 2f) * (Math.Min(w, h) / 2f));
                    }

                    if (inside && px >= 0 && py >= 0 && px < gridWidth && py < gridHeight)
                    {
                        byte val = 255;
                        GL.TexSubImage2D(TextureTarget.Texture2D, 0, px, py, 1, 1,
                                         PixelFormat.Red, PixelType.UnsignedByte, new byte[] { val });
                    }
                }
            }
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        Console.WriteLine($"Random shapes placed: {shapeCount}");
    }


    static void HandleDrawing(GameWindow window, Vector2 mousePos)
    {
        bool left = window.MouseState.IsButtonDown(MouseButton.Left);
        bool right = window.MouseState.IsButtonDown(MouseButton.Right);

        if ((left || right) && lastMousePos.HasValue)
        {
            DrawLineCells(window, lastMousePos.Value, mousePos, left, brushSize);
        }
        else if (left || right)
        {
            // просто одна клетка под курсором
            int cx = (int)(mousePos.X / window.ClientSize.X * gridWidth);
            int cy = (int)((1.0f - mousePos.Y / window.ClientSize.Y) * gridHeight);
            SetCellWithBrush(cx, cy, left, brushSize);
        }

        if (left || right)
            lastMousePos = mousePos;
        else
            lastMousePos = null;
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
        byte value = alive ? (byte)255 : (byte)0;

        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        for (int dx = -brushSize; dx <= brushSize; dx++)
        {
            for (int dy = -brushSize; dy <= brushSize; dy++)
            {
                int x = cx + dx;
                int y = cy + dy;
                if (x >= 0 && y >= 0 && x < gridWidth && y < gridHeight)
                {
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, 1, 1,
                                     PixelFormat.Red, PixelType.UnsignedByte, new byte[] { value });
                }
            }
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }


    static void ResizeGameField(int newW, int newH)
    {
        // --- создаём новые пустые текстуры ---
        int newFront = CreateStateTexture(newW, newH, false);
        int newBack = CreateStateTexture(newW, newH, false);

        ApplyTextureParams(newFront);
        ApplyTextureParams(newBack);

        // --- копируем данные из старой текстуры ---
        if (stateTextureFront != 0)
        {
            // читаем ВСЮ старую текстуру
            byte[] oldData = new byte[gridWidth * gridHeight];
            GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.GetTexImage(TextureTarget.Texture2D, 0,
                           PixelFormat.Red, PixelType.UnsignedByte, oldData);

            // вычисляем, сколько пикселей реально копировать
            int copyW = Math.Min(gridWidth, newW);
            int copyH = Math.Min(gridHeight, newH);

            // создаём буфер под центральный блок
            byte[] copyData = new byte[copyW * copyH];

            for (int y = 0; y < copyH; y++)
            {
                Array.Copy(
                    oldData,                // источник
                    y * gridWidth,          // начало строки в старом поле
                    copyData,               // приёмник
                    y * copyW,              // начало строки в новом блоке
                    copyW                   // сколько байт копировать
                );
            }

            // вставляем в центр новой текстуры
            GL.BindTexture(TextureTarget.Texture2D, newFront);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0,
                             (newW - copyW) / 2, (newH - copyH) / 2,
                             copyW, copyH,
                             PixelFormat.Red, PixelType.UnsignedByte, copyData);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // --- удаляем старые текстуры ---
        if (stateTextureFront != 0) GL.DeleteTexture(stateTextureFront);
        if (stateTextureBack != 0) GL.DeleteTexture(stateTextureBack);

        // --- подменяем ---
        stateTextureFront = newFront;
        stateTextureBack = newBack;
        gridWidth = newW;
        gridHeight = newH;

        PrintInfo();
        //Console.WriteLine($"Resized field: {gridWidth}x{gridHeight}");
    }



    static void DoSimulationStep()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                TextureTarget.Texture2D, stateTextureBack, 0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);

        GL.Viewport(0, 0, gridWidth, gridHeight);

        GL.UseProgram(updateShaderProgram);
        int locState = GL.GetUniformLocation(updateShaderProgram, "currentState");
        int locTexel = GL.GetUniformLocation(updateShaderProgram, "texelSize");

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
        GL.Uniform1(locState, 0);
        GL.Uniform2(locTexel, 1.0f / gridWidth, 1.0f / gridHeight);

        GL.BindVertexArray(quadVAO);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        AddSteps();

        //if (stepCounter % 10 == 0) // каждые 10 шагов, чтобы не спамить
        //    Console.WriteLine($"Step {stepCounter}");

        // swap Front <-> Back
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
        int locState2 = GL.GetUniformLocation(blitShaderProgram, "currentState");
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
        GL.Uniform1(locState2, 0);

        GL.BindVertexArray(quadVAO);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    static void ResetGameField(bool randomInit)
    {
        if (stateTextureFront != 0) GL.DeleteTexture(stateTextureFront);
        if (stateTextureBack != 0) GL.DeleteTexture(stateTextureBack);

        stateTextureFront = CreateStateTexture(gridWidth, gridHeight, randomInit);
        stateTextureBack = CreateStateTexture(gridWidth, gridHeight, false);

        ApplyTextureParams(stateTextureFront);
        ApplyTextureParams(stateTextureBack);
    }

    static void ApplyTextureParams(int tex)
    {
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    static void SetCellState(GameWindow window, Vector2 mousePos, bool alive)
    {
        // экран -> [0..1]
        float nx = mousePos.X / window.ClientSize.X;
        float ny = 1.0f - mousePos.Y / window.ClientSize.Y;

        // [0..1] -> координаты клетки
        int cellX = (int)(nx * gridWidth);
        int cellY = (int)(ny * gridHeight);
        if (cellX < 0 || cellY < 0 || cellX >= gridWidth || cellY >= gridHeight) return;

        byte value = alive ? (byte)255 : (byte)0;

        GL.BindTexture(TextureTarget.Texture2D, stateTextureFront);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1); // 1 байт на пиксель
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, cellX, cellY, 1, 1,
                         PixelFormat.Red, PixelType.UnsignedByte, new byte[] { value });
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    static int CreateStateTexture(int w, int h, bool randomInit)
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);

        // выделяем R8
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, w, h, 0,
                      PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);

        // заполняем данными
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1); // 1 байт/пиксель
        byte[] data = new byte[w * h];
        if (randomInit)
        {
            var rnd = new Random();
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(rnd.Next(2) == 0 ? 255 : 0); // 50% живых для наглядности
        }
        GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h,
                         PixelFormat.Red, PixelType.UnsignedByte, data);

        GL.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    static int CompileShader(string source, ShaderType type)
    {
        int id = GL.CreateShader(type);
        GL.ShaderSource(id, EnsureTrailingNewline(source)); // страховка: \n на конце
        GL.CompileShader(id);
        GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new Exception($"Shader compile error [{type}]:\n{GL.GetShaderInfoLog(id)}");
        return id;
    }

    static int LinkProgram(int vert, int frag)
    {
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vert);
        GL.AttachShader(prog, frag);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0) throw new Exception($"Program link error:\n{GL.GetProgramInfoLog(prog)}");
        return prog;
    }

    // Вшитые ресурсы: "Shaders.filename.ext"
    static string LoadEmbedded(string relative)
    {
        var asm = Assembly.GetExecutingAssembly();
        string baseNs = asm.GetName().Name ?? "";
        string resName = baseNs + "." + relative.Replace('/', '.').Replace('\\', '.');

        using Stream? s = asm.GetManifestResourceStream(resName);
        if (s == null)
        {
            var list = string.Join("\n", asm.GetManifestResourceNames());
            throw new FileNotFoundException($"Embedded resource '{resName}' not found.\nAvailable:\n{list}");
        }
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static string EnsureTrailingNewline(string text) => text.EndsWith("\n") ? text : (text + "\n");
}
