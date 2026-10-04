using System;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace RogueHub
{
    /// <summary>
    /// One read of the controller, keyboard and mouse per frame (Input System, read directly: the game's own menu input
    /// is locked while the hub is open). Directions repeat while held (0.35 s, then 14 a second); a left / right hold
    /// longer than 1.5 s (or Shift) moves values ten steps at a time. Remembers which device was used last, so the
    /// prompt bar shows controller or keyboard hints.
    ///
    /// The devices and their controls are looked up once and kept (looked up again every 2 s, and when a device
    /// changes): every lookup through the interop makes a new wrapper object, so nothing is created per frame while the
    /// menus are closed. Quick mode (while driving) reads only the d-pad, face buttons and arrow / Enter / Esc keys: the
    /// left stick and the triggers are the player's steering and throttle then.
    /// </summary>
    internal static class In
    {
        internal static bool Up, Down, Left, Right;          // pressed this frame or repeating
        internal static bool A, B, X, Y, LB, RB, Start;      // pressed this frame (controller + keyboard equivalents)
        internal static bool Enter, Esc, Back, Del, Tab, ShiftTab, Shift;
        internal static bool PadUsed = true;                 // last input came from a controller
        internal static float FastMult = 1f;                 // x10 on long holds / Shift
        internal static string Typed = "";                   // characters typed this frame (letters, digits, - . , space)
        internal static Vector2 Mouse;                       // screen pixels
        internal static bool Click, RightClick, MouseHeld;
        internal static float Wheel;

        private static float _holdH, _nextV, _nextH;
        private static int _dirV, _dirH;
        private static readonly StringBuilder TypedSb = new StringBuilder();

        // ------------------------------------------------------------------ cached devices and controls
        private static Gamepad _gp;
        private static Keyboard _kb;
        private static UnityEngine.InputSystem.Mouse _ms;
        private static IntPtr _gpPtr, _kbPtr, _msPtr;
        private static float _nextFetch = -1f;
        private static ButtonControl _gUp, _gDown, _gLeft, _gRight, _gA, _gB, _gX, _gY, _gLB, _gRB, _gStart;
        private static StickControl _gStick;
        private static KeyControl _kUp, _kDown, _kLeft, _kRight, _kEnter, _kPadEnter, _kEsc, _kBack, _kDel, _kTab, _kPgUp, _kPgDn,
                                  _kLShift, _kRShift, _kMinus, _kPadMinus, _kPeriod, _kPadPeriod, _kComma, _kSpace;
        private static readonly KeyControl[] Letters = new KeyControl[26];
        private static readonly KeyControl[] Digits = new KeyControl[10], PadDigits = new KeyControl[10];
        private static ButtonControl _mLeft, _mRight;
        private static Vector2Control _mPos, _mScroll;

        /// <summary>Digit d as an Input System key (the enum runs Digit1 .. Digit9, Digit0).</summary>
        private static Key DigitKey(int d) => d == 0 ? Key.Digit0 : (Key)((int)Key.Digit1 + d - 1);

        private static void Fetch()
        {
            float now = Time.unscaledTime;
            if (now < _nextFetch) return;
            _nextFetch = now + 2f;
            var gp = Gamepad.current;
            if (gp == null) { _gp = null; _gpPtr = IntPtr.Zero; }
            else if (gp.Pointer != _gpPtr)
            {
                _gp = gp; _gpPtr = gp.Pointer;
                var dp = gp.dpad;
                _gUp = dp.up; _gDown = dp.down; _gLeft = dp.left; _gRight = dp.right;
                _gA = gp.buttonSouth; _gB = gp.buttonEast; _gX = gp.buttonWest; _gY = gp.buttonNorth;
                _gLB = gp.leftShoulder; _gRB = gp.rightShoulder; _gStart = gp.startButton; _gStick = gp.leftStick;
            }
            var kb = Keyboard.current;
            if (kb == null) { _kb = null; _kbPtr = IntPtr.Zero; }
            else if (kb.Pointer != _kbPtr)
            {
                _kb = kb; _kbPtr = kb.Pointer;
                _kUp = kb.upArrowKey; _kDown = kb.downArrowKey; _kLeft = kb.leftArrowKey; _kRight = kb.rightArrowKey;
                _kEnter = kb.enterKey; _kPadEnter = kb.numpadEnterKey; _kEsc = kb.escapeKey; _kBack = kb.backspaceKey;
                _kDel = kb.deleteKey; _kTab = kb.tabKey; _kPgUp = kb.pageUpKey; _kPgDn = kb.pageDownKey;
                _kLShift = kb.leftShiftKey; _kRShift = kb.rightShiftKey; _kMinus = kb.minusKey; _kPadMinus = kb.numpadMinusKey;
                _kPeriod = kb.periodKey; _kPadPeriod = kb.numpadPeriodKey; _kComma = kb.commaKey; _kSpace = kb.spaceKey;
                for (int i = 0; i < 26; i++) Letters[i] = kb[(Key)((int)Key.A + i)];
                for (int i = 0; i < 10; i++) { Digits[i] = kb[DigitKey(i)]; PadDigits[i] = kb[(Key)((int)Key.Numpad0 + i)]; }
                _openKey = null;
            }
            var ms = UnityEngine.InputSystem.Mouse.current;
            if (ms == null) { _ms = null; _msPtr = IntPtr.Zero; }
            else if (ms.Pointer != _msPtr)
            {
                _ms = ms; _msPtr = ms.Pointer;
                _mLeft = ms.leftButton; _mRight = ms.rightButton; _mPos = ms.position; _mScroll = ms.scroll;
            }
        }

        /// <summary>
        /// A device can be unplugged between fetches: reading its controls then throws, so a removed device is dropped
        /// at once (plain bool, nothing allocated) and the next frame fetches again.
        /// </summary>
        private static void DropRemoved()
        {
            if (_gp != null && !_gp.added) { _gp = null; _gpPtr = IntPtr.Zero; _nextFetch = -1f; }
            if (_kb != null && !_kb.added) { _kb = null; _kbPtr = IntPtr.Zero; _openKey = null; _nextFetch = -1f; }
            if (_ms != null && !_ms.added) { _ms = null; _msPtr = IntPtr.Zero; _nextFetch = -1f; }
        }

        /// <summary>Forget held directions (a menu just opened: nothing left over may fire on its first frame).</summary>
        internal static void Reset()
        {
            _dirV = _dirH = 0;
            Up = Down = Left = Right = A = B = X = Y = LB = RB = Start = false;
            FastMult = 1f;
        }

        /// <summary>typing = keyboard letters are text (search / number entry); quick = driving: d-pad, face buttons and keys only.</summary>
        internal static void Read(bool typing, bool quick = false)
        {
            Fetch();
            DropRemoved();
            float now = Time.unscaledTime;
            bool pu = false, pd = false, pl = false, pr = false;
            A = B = X = Y = LB = RB = Start = false;
            if (_gp != null)
            {
                pu = _gUp.isPressed; pd = _gDown.isPressed; pl = _gLeft.isPressed; pr = _gRight.isPressed;
                if (!quick)
                {
                    Vector2 st = _gStick.ReadValue();
                    pu |= st.y > 0.6f; pd |= st.y < -0.6f; pl |= st.x < -0.6f; pr |= st.x > 0.6f;
                }
                A = _gA.wasPressedThisFrame;
                B = _gB.wasPressedThisFrame;
                X = _gX.wasPressedThisFrame;
                Y = _gY.wasPressedThisFrame;
                LB = _gLB.wasPressedThisFrame;
                RB = _gRB.wasPressedThisFrame;
                Start = _gStart.wasPressedThisFrame;
                if (pu || pd || pl || pr || A || B || X || Y || LB || RB || Start) PadUsed = true;
            }
            Enter = Esc = Back = Del = Tab = ShiftTab = Shift = false;
            TypedSb.Clear();
            bool ku = false, kd = false, kl = false, kr = false;
            if (_kb != null)
            {
                Shift = _kLShift.isPressed || _kRShift.isPressed;
                ku = _kUp.isPressed; kd = _kDown.isPressed; kl = _kLeft.isPressed; kr = _kRight.isPressed;
                Enter = _kEnter.wasPressedThisFrame || _kPadEnter.wasPressedThisFrame;
                Esc = _kEsc.wasPressedThisFrame;
                Back = _kBack.wasPressedThisFrame;
                if (!quick)
                {
                    Del = _kDel.wasPressedThisFrame;
                    if (_kTab.wasPressedThisFrame) { if (Shift) ShiftTab = true; else Tab = true; }
                    if (_kPgUp.wasPressedThisFrame) ShiftTab = true;
                    if (_kPgDn.wasPressedThisFrame) Tab = true;
                    for (int i = 0; i < 26; i++)
                        if (Letters[i] != null && Letters[i].wasPressedThisFrame) TypedSb.Append((char)((Shift ? 'A' : 'a') + i));
                    for (int i = 0; i < 10; i++)
                        if ((Digits[i] != null && Digits[i].wasPressedThisFrame) || (PadDigits[i] != null && PadDigits[i].wasPressedThisFrame)) TypedSb.Append((char)('0' + i));
                    if (_kMinus.wasPressedThisFrame || _kPadMinus.wasPressedThisFrame) TypedSb.Append('-');
                    if (_kPeriod.wasPressedThisFrame || _kPadPeriod.wasPressedThisFrame) TypedSb.Append('.');
                    if (_kComma.wasPressedThisFrame) TypedSb.Append(',');
                    if (_kSpace.wasPressedThisFrame) TypedSb.Append(' ');
                }
                if (ku || kd || kl || kr || Enter || Esc || Back || Del || Tab || ShiftTab || TypedSb.Length > 0) PadUsed = false;
            }
            Typed = TypedSb.Length == 0 ? "" : TypedSb.ToString();

            // keyboard equivalents of the face buttons (Delete = reset only when not typing: then it edits text)
            if (Enter) A = true;
            if (Esc) B = true;
            if (Del && !typing) Y = true;

            int v = (pu || ku) ? 1 : (pd || kd) ? -1 : 0;
            int h = (pr || kr) ? 1 : (pl || kl) ? -1 : 0;
            Up = Down = Left = Right = false;
            if (v != 0)
            {
                if (v != _dirV) { _dirV = v; _nextV = now + 0.35f; Fire(v, true); }
                else if (now >= _nextV) { _nextV = now + 0.07f; Fire(v, true); }
            }
            else _dirV = 0;
            if (h != 0)
            {
                if (h != _dirH) { _dirH = h; _holdH = now; _nextH = now + 0.35f; Fire(h, false); }
                else if (now >= _nextH) { _nextH = now + 0.07f; Fire(h, false); }
            }
            else _dirH = 0;
            FastMult = Shift || (h != 0 && now - _holdH > 1.5f) ? 10f : 1f;

            Click = RightClick = MouseHeld = false;
            Wheel = 0f;
            if (_ms != null && !quick)
            {
                Mouse = _mPos.ReadValue();
                Click = _mLeft.wasPressedThisFrame;
                RightClick = _mRight.wasPressedThisFrame;
                MouseHeld = _mLeft.isPressed;
                Wheel = _mScroll.ReadValue().y;
                if (Click || RightClick || Wheel != 0f) PadUsed = false;
            }
        }

        private static void Fire(int dir, bool vertical)
        {
            if (vertical) { if (dir > 0) Up = true; else Down = true; }
            else { if (dir > 0) Right = true; else Left = true; }
        }

        // ------------------------------------------------------------------ LB + RB (polled every frame while closed)

        private static float _lbAt = -10f, _rbAt = -10f;
        private const float ChordWindow = 0.15f;

        /// <summary>
        /// LB and RB pressed together: both presses within 0.15 s. Holding one bumper and pressing the other later (how
        /// DriverCam's Edit mode uses them as modifiers) doesn't count. Cached controls only: nothing allocated.
        /// </summary>
        internal static bool Chord()
        {
            Fetch();
            DropRemoved();
            if (_gp == null) return false;
            float now = Time.unscaledTime;
            if (_gLB.wasPressedThisFrame) _lbAt = now;
            if (_gRB.wasPressedThisFrame) _rbAt = now;
            bool justNow = _lbAt == now || _rbAt == now;
            if (!justNow || Mathf.Abs(_lbAt - _rbAt) > ChordWindow || !_gLB.isPressed || !_gRB.isPressed) return false;
            _lbAt = _rbAt = -10f;   // one chord per press
            return true;
        }

        // ------------------------------------------------------------------ the open key (polled every frame while closed)

        private static KeyControl _openKey;
        private static Key _openKeyName = Key.None;

        /// <summary>The quick-menu key pressed this frame. A key that types text or drives the menus can't be the open key (backtick then).</summary>
        internal static bool OpenKey(Key configured, out bool shift)
        {
            shift = false;
            Fetch();
            DropRemoved();
            if (_kb == null) return false;
            Key k = Usable(configured) ? configured : Key.Backquote;
            if (_openKey == null || k != _openKeyName)
            {
                _openKeyName = k;
                _openKey = _kb[k];
            }
            if (_openKey == null || !_openKey.wasPressedThisFrame) return false;
            shift = _kLShift.isPressed || _kRShift.isPressed;
            return true;
        }

        internal static bool Usable(Key k) =>
            k != Key.None && !(k >= Key.A && k <= Key.Z) && !(k >= Key.Digit1 && k <= Key.Digit0) && !(k >= Key.Numpad0 && k <= Key.Numpad9) &&
            k != Key.Enter && k != Key.NumpadEnter && k != Key.Escape && k != Key.Space && k != Key.Backspace && k != Key.Tab &&
            k != Key.Delete && k != Key.UpArrow && k != Key.DownArrow && k != Key.LeftArrow && k != Key.RightArrow &&
            k != Key.Minus && k != Key.Period && k != Key.Comma && k != Key.NumpadMinus && k != Key.NumpadPeriod &&
            k != Key.LeftShift && k != Key.RightShift && k != Key.PageUp && k != Key.PageDown;

        /// <summary>The first keyboard key pressed this frame (key binding), or Key.None. Only while capturing a key.</summary>
        internal static Key AnyKey()
        {
            Fetch();
            DropRemoved();
            if (_kb == null) return Key.None;
            for (int k = (int)Key.Space; k <= (int)Key.OEM5; k++)   // every key up to the function and OEM keys
            {
                KeyControl c;
                try { c = _kb[(Key)k]; } catch { continue; }
                if (c != null && c.wasPressedThisFrame) return (Key)k;
            }
            return Key.None;
        }
    }
}
