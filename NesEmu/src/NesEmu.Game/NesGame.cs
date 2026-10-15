using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NesEmu.Core;

/// <summary>
/// Janela do emulador. O MonoGame chama Update() 60 vezes por segundo: a cada chamada lemos o
/// teclado/gamepad, rodamos UM quadro do NES e, em Draw(), mostramos o resultado ampliado.
/// </summary>
public sealed class NesGame : Game
{
    private const int Scale = 3;

    private readonly Nes _nes;
    private readonly string _title;
    private readonly uint[] _pixels = new uint[Ppu.Width * Ppu.Height];
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _texture = null!;
    private bool _crashed;

    // Áudio: o APU gera ~735 amostras por quadro (44100 Hz / 60 quadros) e enviamos a cada Update
    private DynamicSoundEffectInstance? _audio;
    private readonly short[] _samples = new short[4096];
    private readonly byte[] _audioBytes = new byte[4096 * 2];

    public NesGame(Nes nes, string title)
    {
        _nes = nes;
        _title = title;

        var graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Ppu.Width * Scale,
            PreferredBackBufferHeight = Ppu.Height * Scale
        };
        graphics.ApplyChanges();

        Window.AllowUserResizing = true;
        Window.Title = $"NesEmu - {title}  (Esc = sair)";
        IsMouseVisible = true;

        // Casa a produção de som com o vídeo: exatamente 44100/60 amostras por quadro emulado
        _nes.Apu.CyclesPerSample = 29780.5 / (Apu.SampleRate / 60.0);
        // IsFixedTimeStep já é true e o passo padrão é 1/60 s, quase igual aos ~60,1 Hz do NES.
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _texture = new Texture2D(GraphicsDevice, Ppu.Width, Ppu.Height, false, SurfaceFormat.Color);

        try
        {
            _audio = new DynamicSoundEffectInstance(Apu.SampleRate, AudioChannels.Mono);
            _audio.Play();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Sem áudio: {e.Message}");
            _audio = null;
        }
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape)) Exit();

        if (!_crashed)
        {
            _nes.Bus.Controller1 = ReadInput();
            try
            {
                _nes.RunFrame();
                SubmitAudio();
            }
            catch (NotSupportedException e)
            {
                _crashed = true;
                _audio?.Stop();
                Window.Title = $"NesEmu - {_title} - PAROU: {e.Message}";
                Console.WriteLine(e.Message);
            }
        }

        base.Update(gameTime);
    }

    private void SubmitAudio()
    {
        int n = _nes.Apu.ReadSamples(_samples);     // sempre esvazia, para o APU não acumular
        if (_audio == null || n == 0) return;
        if (_audio.PendingBufferCount > 6) return;  // muito atraso acumulado: descarta para não aumentar a latência

        Buffer.BlockCopy(_samples, 0, _audioBytes, 0, n * 2);
        _audio.SubmitBuffer(_audioBytes, 0, n * 2);
    }

    protected override void Draw(GameTime gameTime)
    {
        // A PPU guarda 0xAARRGGBB; a textura do MonoGame espera os bytes R,G,B,A (0xAABBGGRR): troca R e B.
        var frame = _nes.Ppu.Frame;
        for (int i = 0; i < frame.Length; i++)
        {
            uint p = frame[i];
            _pixels[i] = (p & 0xFF00FF00u) | ((p & 0xFFu) << 16) | ((p >> 16) & 0xFFu);
        }
        _texture.SetData(_pixels);

        GraphicsDevice.Clear(Color.Black);

        // Mantém a proporção 256x240, centralizada, mesmo se a janela for redimensionada
        int w = GraphicsDevice.Viewport.Width;
        int h = GraphicsDevice.Viewport.Height;
        float scale = MathF.Min(w / (float)Ppu.Width, h / (float)Ppu.Height);
        int dw = (int)(Ppu.Width * scale);
        int dh = (int)(Ppu.Height * scale);
        var destination = new Rectangle((w - dw) / 2, (h - dh) / 2, dw, dh);

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp);   // pixels nítidos, sem suavização
        _spriteBatch.Draw(_texture, destination, Color.White);
        _spriteBatch.End();

        base.Draw(gameTime);
    }

    /// <summary>
    /// Teclado: setas ou WASD = direcional, X = A, Z = B, Enter = Start, Shift direito ou Backspace = Select.
    /// Gamepad: direcional, A = A, X/B = B, Start, Back = Select.
    /// </summary>
    private static NesButton ReadInput()
    {
        var kb = Keyboard.GetState();
        var pad = GamePad.GetState(PlayerIndex.One);
        var b = NesButton.None;

        if (kb.IsKeyDown(Keys.X) || pad.Buttons.A == ButtonState.Pressed) b |= NesButton.A;
        if (kb.IsKeyDown(Keys.Z) || pad.Buttons.X == ButtonState.Pressed || pad.Buttons.B == ButtonState.Pressed) b |= NesButton.B;
        if (kb.IsKeyDown(Keys.RightShift) || kb.IsKeyDown(Keys.Back) || pad.Buttons.Back == ButtonState.Pressed) b |= NesButton.Select;
        if (kb.IsKeyDown(Keys.Enter) || pad.Buttons.Start == ButtonState.Pressed) b |= NesButton.Start;
        if (kb.IsKeyDown(Keys.Up) || kb.IsKeyDown(Keys.W) || pad.DPad.Up == ButtonState.Pressed) b |= NesButton.Up;
        if (kb.IsKeyDown(Keys.Down) || kb.IsKeyDown(Keys.S) || pad.DPad.Down == ButtonState.Pressed) b |= NesButton.Down;
        if (kb.IsKeyDown(Keys.Left) || kb.IsKeyDown(Keys.A) || pad.DPad.Left == ButtonState.Pressed) b |= NesButton.Left;
        if (kb.IsKeyDown(Keys.Right) || kb.IsKeyDown(Keys.D) || pad.DPad.Right == ButtonState.Pressed) b |= NesButton.Right;

        // O controle real não permite direções opostas ao mesmo tempo, e alguns jogos travam se isso acontecer
        if ((b & (NesButton.Up | NesButton.Down)) == (NesButton.Up | NesButton.Down))
            b &= ~(NesButton.Up | NesButton.Down);
        if ((b & (NesButton.Left | NesButton.Right)) == (NesButton.Left | NesButton.Right))
            b &= ~(NesButton.Left | NesButton.Right);

        return b;
    }
}
