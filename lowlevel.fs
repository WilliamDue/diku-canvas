module Lowlevel

open System.Runtime.InteropServices
open System
open System.Collections.Generic

open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open SixLabors.ImageSharp.Drawing.Processing
open SixLabors.ImageSharp.Drawing
// colors
type Color = SixLabors.ImageSharp.Color

type InternalEvent = internal InternalEvent of SDL.SDL_Event

let fromRgba (red:int) (green:int) (blue:int) (a:int) : Color =
    Color.FromRgba(byte red, byte green, byte blue, byte a)
let fromRgb (red:int) (green:int) (blue:int) : Color =
    Color.FromRgb(byte red, byte green, byte blue)
type image = SixLabors.ImageSharp.Image<Rgba32>
type DrawingContext = internal DrawingContext of SixLabors.ImageSharp.Processing.IImageProcessingContext
type drawing_fun = DrawingContext -> DrawingContext

type Font = SixLabors.Fonts.Font
type FontFamily = SixLabors.Fonts.FontFamily
let systemFontNames: string list = [for i in  SixLabors.Fonts.SystemFonts.Families do yield i.Name]
let getFamily name = SixLabors.Fonts.SystemFonts.Get(name)
let makeFont (fam:FontFamily) (size:float) = fam.CreateFont(float32 size, SixLabors.Fonts.FontStyle.Regular)
let measureText (f:Font) (txt:string) = 
    let rect = SixLabors.Fonts.TextMeasurer.MeasureSize(txt, SixLabors.Fonts.TextOptions(f))
    (float rect.Width,float rect.Height)

type Tool = 
    | Pen of SixLabors.ImageSharp.Drawing.Processing.Pen
    | Brush of SixLabors.ImageSharp.Drawing.Processing.Brush

let solidBrush (color:Color) : Tool =
    Brush (Brushes.Solid(color))
let solidPen (color:Color) (width:float) : Tool =
    Pen (Pens.Solid(color, float32 width))

type point = int * int
type pointF = float * float
type Vector2 = System.Numerics.Vector2
type Matrix3x2 = System.Numerics.Matrix3x2
type Matrix4x4 = System.Numerics.Matrix4x4
type TextOptions = SixLabors.Fonts.TextOptions

let toPointF (x:float, y:float) = PointF(x = float32 x, y = float32 y)
let toVector2 (x:float, y:float) = System.Numerics.Vector2(float32 x, float32 y)

type PathDefinition =
    | EmptyDef
    | ArcTo of float * float * float * bool * bool * pointF
    | CubicBezierTo of pointF * pointF * pointF
    | LineTo of pointF
    | MoveTo of pointF
    | QuadraticBezierTo of pointF * pointF
    | SetTransform of Matrix3x2
    | LocalTransform of Matrix3x2 * PathDefinition
    | StartFigure
    | CloseFigure
    | CloseAllFigures
    | Combine of PathDefinition * PathDefinition

let (<++>) p1 p2 =
    match p1, p2 with
        | EmptyDef, _ -> p2
        | _, EmptyDef -> p1
        | defs -> Combine defs

let construct pd : IPath =
    let initial = Matrix3x2.Identity
    let builder = PathBuilder initial
    let rec loop (builder:PathBuilder) curT = function // tail-recursive traversal
        | [] -> builder
        | cur :: worklist ->
            match cur with
                | EmptyDef ->
                    loop builder curT worklist
                | ArcTo(radiusX, radiusY, rotation, largeArc, sweep, point) ->
                    let builder = builder.ArcTo(float32 radiusX, float32 radiusY,
                                                float32 rotation, largeArc, sweep,
                                                toPointF point)
                    loop builder curT worklist
                | CubicBezierTo(secondControlPoint, thirdControlPoint, point) ->
                    let builder = builder.CubicBezierTo(toVector2 secondControlPoint,
                                                        toVector2 thirdControlPoint,
                                                        toVector2 point)
                    loop builder curT worklist
                | LineTo p ->
                    let builder = builder.LineTo(toPointF p)
                    loop builder curT worklist
                | MoveTo p ->
                    let builder = builder.MoveTo(toPointF p)
                    loop builder curT worklist
                | QuadraticBezierTo(secondControlPoint, point) ->
                    let builder = builder.QuadraticBezierTo(toVector2 secondControlPoint,
                                                            toVector2 point)
                    loop builder curT worklist
                | SetTransform mat ->
                    let builder = builder.SetTransform mat
                    loop builder mat worklist
                | LocalTransform (mat, pd) ->
                    let builder = builder.SetTransform mat
                    loop builder mat (pd :: SetTransform curT :: worklist)
                | StartFigure ->
                    let builder = builder.StartFigure()
                    loop builder curT worklist
                | CloseFigure ->
                    let builder = builder.CloseFigure()
                    loop builder curT worklist
                | CloseAllFigures ->
                    let builder = builder.CloseAllFigures()
                    loop builder curT worklist
                | Combine (p1, p2) ->
                    loop builder curT (p1 :: p2 :: worklist)
    (loop builder initial pd).Build()

type PrimPath =
    | Arc of pointF * float * float * float * float * float
    | CubicBezier of pointF * pointF * pointF * pointF
    | Line of pointF * pointF
    | Lines of pointF list

and PathTree =
    | Empty
    | Prim of Tool * PrimPath
    | PathAdd of PathTree * PathTree
    | Transform of Matrix3x2 * PathTree
    | Text of Tool * string * TextOptions  // FIXME: Maybe we want a `Raw of IPathCollection` constructor instead of the Text constructors
    | TextAlong of Tool * string * TextOptions * PrimPath

let (<+>) p1 p2 =
    match p1, p2 with
        | Empty, _ -> p2
        | _, Empty -> p1
        | _ -> PathAdd (p1, p2)

let pathAdd = (<+>)

let emptyPath = Empty

let pathFromList ps = List.fold pathAdd emptyPath ps

let transform (mat:Matrix3x2) = function
    | Transform (cur, p) ->
        if mat.IsIdentity then Transform(cur, p)
        else let res = mat * cur
             if res.IsIdentity then p
             else Transform(res, p)
    | p ->
        Transform(mat, p)

let rotateDegreeAround (degrees:float) point p =
    let mat = Matrix3x2Extensions.CreateRotationDegrees(float32 degrees, toPointF point)
    transform mat p

let rotateRadiansAround (radians:float) point p =
    let mat = Matrix3x2Extensions.CreateRotation(float32 radians, toPointF point)
    transform mat p

let toILineSegment : PrimPath -> ILineSegment = function
    | Arc(center, rX, rY, rotation, start, sweep) ->
        ArcLineSegment(toPointF center, SizeF(float32 rX, float32 rY), float32 rotation, float32 start, float32 sweep)
    | CubicBezier(start, c1, c2, endPoint) ->
        CubicBezierLineSegment(toPointF start, toPointF c1, toPointF c2, toPointF endPoint, [||])
    | Line(start, endP) ->
        LinearLineSegment(toPointF start, toPointF endP)
    | Lines points ->
        LinearLineSegment(points |> Seq.map toPointF |> Seq.toArray)

let toPath (ilineseg:ILineSegment) : IPath =
    Path [| ilineseg |]

let flatten (p : PathTree) : (Tool*IPathCollection) list =  //FIXME: should maybe return a seq<IPathCollection>
    let rec traverse (acc : (Tool*IPathCollection) list) = function // tail-recursive traversal
        | [] -> List.rev acc
        | cur :: worklist ->
            match cur with
                | Empty ->
                    traverse acc worklist
                | Prim (pen, p) ->
                    let path = PathCollection [| p |> toILineSegment |> toPath |] : IPathCollection
                    let acc =  (pen,path) :: acc
                    traverse acc worklist
                | PathAdd (p1, p2) ->
                    traverse acc (p1 :: p2 :: worklist)
                | Transform (mat, p) ->
                    let transformed = traverse [] [p] |> List.map (fun (pen,p) -> (pen,p.Transform(mat)))
                    let acc = transformed @ acc
                    traverse acc worklist
                | Text (pen, text, options) ->
                    let glyphs = TextBuilder.GenerateGlyphs(text, options)
                    let acc =  (pen,glyphs) :: acc
                    traverse acc worklist
                | TextAlong(pen, text, options, p) ->
                    let path = p |> toILineSegment |> toPath
                    let glyphs = TextBuilder.GenerateGlyphs(text, path, options)
                    let acc =  (pen, glyphs) :: acc
                    traverse acc worklist
    traverse [] [p]

let drawCollection ((tool,col):Tool * IPathCollection) (ctx: DrawingContext): DrawingContext =
    let (DrawingContext _ctx) = ctx
    DrawingContext <|
    match tool with
        | Pen pen ->
            _ctx.Draw(pen, col)
        | Brush brush ->
            _ctx.Fill(DrawingOptions(), brush, col)

let drawPathTree (paths:PathTree) (ctx: DrawingContext) : DrawingContext =
    flatten paths |> List.fold (fun ctx penNCol -> drawCollection penNCol ctx) ctx

let drawToFile width height (filePath:string) (draw: drawing_fun) =
    let img = new Image<Rgba32>(width, height)
    img.Mutate(DrawingContext >> draw >> ignore)
    img.Save(filePath)

let drawToAnimatedGif width heigth (frameDelay:int) (repeatCount:int) (filePath:string) (drawLst:drawing_fun list) =
    match drawLst with
        draw::rst ->
            let gif = new Image<Rgba32>(width, heigth)
            gif.Mutate(DrawingContext >> draw >> ignore)
            let gifMetaData = gif.Metadata.GetGifMetadata()
            gifMetaData.RepeatCount <- uint16 repeatCount
            let metadata = gif.Frames.RootFrame.Metadata.GetGifMetadata()
            metadata.FrameDelay <- frameDelay
            List.iter (fun (draw:drawing_fun) -> 
                let frame = new Image<Rgba32>(width, heigth)
                frame.Mutate(DrawingContext >> draw >> ignore)
                let metadata = frame.Frames.RootFrame.Metadata.GetGifMetadata()
                metadata.FrameDelay <- frameDelay
                gif.Frames.AddFrame(frame.Frames.RootFrame) |> ignore
            ) rst
            gif.SaveAsGif(filePath)
        | _ -> ()

type Text(position: Vector2, text: string, color: Color, fontFamily: FontFamily, size: float) =
    let mutable fontFamily = fontFamily 
    let mutable font = makeFont fontFamily size
    let mutable path = TextBuilder.GenerateGlyphs(text, TextOptions font)
    let mutable brush = Brushes.Solid(color)
    let mutable text = text
    do
        let pos = position - Vector2(path.Bounds.Location.X, path.Bounds.Location.Y)
        path <- path.Translate(pos)

    member private this.UpdatePath () =
        path <- TextBuilder.GenerateGlyphs(text, TextOptions font)
                .Translate(position)

    member private this.UpdateFont () =
        font <- makeFont fontFamily size

    member this.Text
        with get () = text
        and set v =
            text <- v
            this.UpdatePath ()
    
    member this.FontFamily
        with get () = fontFamily
        and set v =
            fontFamily <- v
            this.UpdateFont ()
            this.UpdatePath ()

    member this.Color
        with get () = color
        and set v = brush <- Brushes.Solid(v)

    member this.Render (DrawingContext ctx) =
        ctx.Fill(DrawingOptions(), brush, path)
        |> ignore
    
    member this.Extent
        with get () = Vector2(path.Bounds.Width, path.Bounds.Height)
    
    member this.Position
        with get () = Vector2(path.Bounds.Location.X, path.Bounds.Location.Y)
    
    member this.Scale (scaling: Vector2) =
        path <- path.Scale(scaling.X, scaling.Y)
        this.Position
            
    member this.Translate (translation: Vector2) =
        path <- path.Scale(translation.X, translation.Y)
        this.Extent
    
    member this.SetExtent (newExtent: Vector2) =
        path <- path.Scale(
            newExtent.X / path.Bounds.Width,
            newExtent.Y / path.Bounds.Height
        )
    
    member this.SetPosition (newPosition: Vector2) =
        path <- path.Scale(
            newPosition.X - path.Bounds.Location.X,
            newPosition.Y - path.Bounds.Location.X
        )
(*
type Texture(position: Vector2, stream: IO.Stream) =
    let mutable image = Image.Load(stream)
    let mutable position = position
    let sampler = Processors.Transforms.NearestNeighborResampler ()
    do
        image.Mutate(fun ctx -> ctx |> ignore )
    
    interface IGraphic with
        member this.Render (DrawingContext ctx) =
            let position = Point(50, 50)
            ctx.DrawImage(image, position, 1f)
            |> ignore
        
        member this.Extent
            with get () = Vector2(float32 image.Bounds.Width, float32 image.Bounds.Height)
        
        member this.Position
            with get () = Vector2(float32 image.Bounds.Location.X, float32 image.Bounds.Location.Y)
        
        member this.Scale scaling =
            image.Mutate(fun ctx ->
                ctx.Resize(int scaling.X, int scaling.Y, sampler)
                |> ignore
            )
            path <- path.Scale(scaling.X, scaling.Y)
            Vector2(path.Bounds.Width, path.Bounds.Height)
                
        member this.Translate translation =
            path <- path.Scale(translation.X, translation.Y)
            Vector2(path.Bounds.Location.X, path.Bounds.Location.Y)
        
        member this.SetExtent (newExtent: Vector2) =
            path <- path.Scale(
                newExtent.X / path.Bounds.Width,
                newExtent.Y / path.Bounds.Height
            )
        
        member this.SetPosition (newPosition: Vector2) =
            path <- path.Scale(
                newPosition.X - path.Bounds.Location.X,
                newPosition.Y - path.Bounds.Location.X
            )
*)

type KeyAction =
    | KeyPress = 0
    | KeyRelease = 1

type KeyboardKey =
    | Unknown = 0 // Matches the case of an invalid, non-existent, or unsupported keyboard key.
    | Space = 1 // The spacebar key
    | Apostrophe = 2 // The apostrophe key '''
    | Comma = 3 // The comma key ','
    | Minus = 4 // The minus key '-'
    | Plus = 5 // The plus key '+'
    | Period = 6 // The period/dot key '.'
    | Slash = 7 // The slash key '/'
    | Num0 = 8 // The 0 key.
    | Num1 = 9 // The 1 key.
    | Num2 = 10 // The 2 key.
    | Num3 = 11 // The 3 key.
    | Num4 = 12 // The 4 key.
    | Num5 = 13 // The 5 key.
    | Num6 = 14 // The 6 key.
    | Num7 = 15 // The 7 key.
    | Num8 = 16 // The 8 key.
    | Num9 = 17 // The 9 key.
    | Semicolon = 18 // The semicolon key.
    | Equal = 19 // The equal key.
    | A = 20 // The A key.
    | B = 21 // The B key.
    | C = 22 // The C key.
    | D = 23 // The D key.
    | E = 24 // The E key.
    | F = 25 // The F key.
    | G = 26 // The G key.
    | H = 27 // The H key.
    | I = 28 // The I key.
    | J = 29 // The J key.
    | K = 30 // The K key.
    | L = 31 // The L key.
    | M = 32 // The M key.
    | N = 33 // The N key.
    | O = 34 // The O key.
    | P = 35 // The P key.
    | Q = 36 // The Q key.
    | R = 37 // The R key.
    | S = 38 // The S key.
    | T = 39 // The T key.
    | U = 40 // The U key.
    | V = 41 // The V key.
    | W = 42 // The W key.
    | X = 43 // The X key.
    | Y = 44 // The Y key.
    | Z = 45 // The Z key.
    | LeftBracket = 46 // The left bracket(opening bracket) key '['
    | Backslash = 47 // The backslash '\'
    | RightBracket = 48 // The right bracket(closing bracket) key ']'
    | GraveAccent = 49 // The grave accent key '`'
    | AcuteAccent = 50 // The acute accent key ("inverted" grave accent) '´'
    | Escape = 51 // The escape key.
    | Enter = 52 // The enter key.
    | Tab = 53 // The tab key.
    | Backspace = 54 // The backspace key.
    | Insert = 55 // The insert key.
    | Delete = 56 // The delete key.
    | Right = 57 // The right arrow key.
    | Left = 58 // The left arrow key.
    | Down = 59 // The down arrow key.
    | Up = 60 // The up arrow key.
    | PageUp = 61 // The page up key.
    | PageDown = 62 // The page down key.
    | Home = 63 // The home key.
    | End = 64 // The end key.
    | CapsLock = 65 // The caps lock key.
    | ScrollLock = 66 // The scroll lock key.
    | NumLock = 67 // The num lock key.
    | PrintScreen = 68 // The print screen key.
    | Pause = 69 // The pause key.
    | F1 = 70 // The F1 key.
    | F2 = 71 // The F2 key.
    | F3 = 72 // The F3 key.
    | F4 = 73 // The F4 key.
    | F5 = 74 // The F5 key.
    | F6 = 75 // The F6 key.
    | F7 = 76 // The F7 key.
    | F8 = 77 // The F8 key.
    | F9 = 78 // The F9 key.
    | F10 = 79 // The F10 key.
    | F11 = 80 // The F11 key.
    | F12 = 81 // The F12 key.
    | KeyPad0 = 82 // The 0 key on the key pad.
    | KeyPad1 = 83 // The 1 key on the key pad.
    | KeyPad2 = 84 // The 2 key on the key pad.
    | KeyPad3 = 85 // The 3 key on the key pad.
    | KeyPad4 = 86 // The 4 key on the key pad.
    | KeyPad5 = 87 // The 5 key on the key pad.
    | KeyPad6 = 88 // The 6 key on the key pad.
    | KeyPad7 = 89 // The 7 key on the key pad.
    | KeyPad8 = 90 // The 8 key on the key pad.
    | KeyPad9 = 91 // The 9 key on the key pad.
    | KeyPadDecimal = 92 // The decimal key on the key pad.
    | KeyPadDivide = 93 // The divide key on the key pad.
    | KeyPadMultiply = 94 // The multiply key on the key pad.
    | KeyPadSubtract = 95 // The subtract key on the key pad.
    | KeyPadAdd = 96 // The add key on the key pad.
    | KeyPadEnter = 97 // The enter key on the key pad.
    | KeyPadEqual = 98 // The equal key on the key pad.
    | LeftShift = 99 // The left shift key.
    | LeftControl = 100 // The left control key.
    | LeftAlt = 101 // The left alt key.
    | LeftSuper = 102 // The left super key.
    | RightShift = 103 // The right shift key.
    | RightControl = 104 // The right control key.
    | RightAlt = 105 // The right alt key.
    | RightSuper = 106 // The right super key.
    | Menu = 107 // The menu key.
    | Diaresis = 108 // The Diaresis key '¨'
    | LessThan = 109 // The less than sign '<'
    | GreaterThan = 110 // The greater than sign '>'
    | FractionOneHalf = 111 // The "vulgar fraction one half" key '½'
    | DanishAA = 112 // The Danish AA key 'Å'
    | DanishAE = 113 // The Danish AE key 'Æ'
    | DanishOE = 114 // The Danish OE key 'Ø'


let private toKeyboardKey key =
    match SDL.stringFromKeyboard key with
    | "Space" -> KeyboardKey.Space
    | "'" -> KeyboardKey.Apostrophe
    | "," -> KeyboardKey.Comma
    | "-" -> KeyboardKey.Minus
    | "+" -> KeyboardKey.Plus
    | "." -> KeyboardKey.Period
    | "/" -> KeyboardKey.Slash
    | "0" -> KeyboardKey.Num0
    | "1" -> KeyboardKey.Num1
    | "2" -> KeyboardKey.Num2
    | "3" -> KeyboardKey.Num3
    | "4" -> KeyboardKey.Num4
    | "5" -> KeyboardKey.Num5
    | "6" -> KeyboardKey.Num6
    | "7" -> KeyboardKey.Num7
    | "8" -> KeyboardKey.Num8
    | "9" -> KeyboardKey.Num9
    | ";" -> KeyboardKey.Semicolon
    | "=" -> KeyboardKey.Equal
    | "A" -> KeyboardKey.A
    | "B" -> KeyboardKey.B
    | "C" -> KeyboardKey.C
    | "D" -> KeyboardKey.D
    | "E" -> KeyboardKey.E
    | "F" -> KeyboardKey.F
    | "G" -> KeyboardKey.G
    | "H" -> KeyboardKey.H
    | "I" -> KeyboardKey.I
    | "J" -> KeyboardKey.J
    | "K" -> KeyboardKey.K
    | "L" -> KeyboardKey.L
    | "M" -> KeyboardKey.M
    | "N" -> KeyboardKey.N
    | "O" -> KeyboardKey.O
    | "P" -> KeyboardKey.P
    | "Q" -> KeyboardKey.Q
    | "R" -> KeyboardKey.R
    | "S" -> KeyboardKey.S
    | "T" -> KeyboardKey.T
    | "U" -> KeyboardKey.U
    | "V" -> KeyboardKey.V
    | "W" -> KeyboardKey.W
    | "X" -> KeyboardKey.X
    | "Y" -> KeyboardKey.Y
    | "Z" -> KeyboardKey.Z
    | "[" -> KeyboardKey.LeftBracket
    | "\\" -> KeyboardKey.Backslash
    | "]" -> KeyboardKey.RightBracket
    | "`" -> KeyboardKey.GraveAccent
    | "´" -> KeyboardKey.AcuteAccent
    | "Escape" -> KeyboardKey.Escape
    | "Return" -> KeyboardKey.Enter // This should probably be Return.
    | "Tab" -> KeyboardKey.Tab
    | "Backspace" -> KeyboardKey.Backspace
    | "Insert" -> KeyboardKey.Insert
    | "Delete" -> KeyboardKey.Delete
    | "Right" -> KeyboardKey.Right
    | "Left" -> KeyboardKey.Left
    | "Down" -> KeyboardKey.Down
    | "Up" -> KeyboardKey.Up
    | "PageUp" -> KeyboardKey.PageUp
    | "PageDown" -> KeyboardKey.PageDown
    | "Home" -> KeyboardKey.Home
    | "End" -> KeyboardKey.End
    | "CapsLock" -> KeyboardKey.CapsLock
    | "ScrollLock" -> KeyboardKey.ScrollLock
    | "Numlock" -> KeyboardKey.NumLock // Has to be lowercase.
    | "PrintScreen" -> KeyboardKey.PrintScreen
    | "Pause" -> KeyboardKey.Pause
    | "F1" -> KeyboardKey.F1
    | "F2" -> KeyboardKey.F2
    | "F3" -> KeyboardKey.F3
    | "F4" -> KeyboardKey.F4
    | "F5" -> KeyboardKey.F5
    | "F6" -> KeyboardKey.F6
    | "F7" -> KeyboardKey.F7
    | "F8" -> KeyboardKey.F8
    | "F9" -> KeyboardKey.F9
    | "F10" -> KeyboardKey.F10
    | "F11" -> KeyboardKey.F11
    | "F12" -> KeyboardKey.F12
    | "Keypad 0" -> KeyboardKey.KeyPad0
    | "Keypad 1" -> KeyboardKey.KeyPad1
    | "Keypad 2" -> KeyboardKey.KeyPad2
    | "Keypad 3" -> KeyboardKey.KeyPad3
    | "Keypad 4" -> KeyboardKey.KeyPad4
    | "Keypad 5" -> KeyboardKey.KeyPad5
    | "Keypad 6" -> KeyboardKey.KeyPad6
    | "Keypad 7" -> KeyboardKey.KeyPad7
    | "Keypad 8" -> KeyboardKey.KeyPad8
    | "Keypad 9" -> KeyboardKey.KeyPad9
    | "Keypad ." -> KeyboardKey.KeyPadDecimal
    | "Keypad /" -> KeyboardKey.KeyPadDivide
    | "Keypad *" -> KeyboardKey.KeyPadMultiply
    | "Keypad -" -> KeyboardKey.KeyPadSubtract
    | "Keypad +" -> KeyboardKey.KeyPadAdd
    | "Keypad Enter" -> KeyboardKey.KeyPadEnter
    | "Keypad =" -> KeyboardKey.KeyPadEqual
    | "Left Shift" -> KeyboardKey.LeftShift
    | "Left Ctrl" -> KeyboardKey.LeftControl
    | "Left Alt" -> KeyboardKey.LeftAlt
    | "Left GUI" -> KeyboardKey.LeftSuper
    | "Right Shift" -> KeyboardKey.RightShift
    | "Right Ctrl" -> KeyboardKey.RightControl
    | "Right Alt" -> KeyboardKey.RightAlt
    | "Right GUI" -> KeyboardKey.RightSuper
    | "Menu" -> KeyboardKey.Menu // Not sure if this does anything.
    | "¨" -> KeyboardKey.Diaresis
    | "<" -> KeyboardKey.LessThan
    | ">" -> KeyboardKey.GreaterThan
    | "½" -> KeyboardKey.FractionOneHalf
    | "å" -> KeyboardKey.DanishAA
    | "æ" -> KeyboardKey.DanishAE
    | "ø" -> KeyboardKey.DanishOE
    | _ -> KeyboardKey.Unknown

let private TIMER_EVENT =
    match SDL.SDL_RegisterEvents 1 with
    | UInt32.MaxValue -> failwith "Error: Could not allocate a user-defined Timer Event."
    | x -> x

let timerTickEvent () =
    let mutable ev = SDL.SDL_Event()
    ev.``type`` <- TIMER_EVENT
    SDL.SDL_PushEvent(&ev) |> ignore

type Window(t:string, w:int, h:int) =
    let mutable disposed = false
    static let mutable eventQueues = Map.empty
    let viewWidth, viewHeight = w, h
    let mutable window, renderer, texture, bufferPtr = IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero
    let mutable frameBuffer = Array.create 0 (byte 0)
    let mutable event = SDL.SDL_Event()
    let mutable img = None
    let mutable windowId = 0u
    let mutable background = Color.Black
    let windowFlags =
        SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN |||
        SDL.SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS
    do
        let is_initial = 0 = Map.count eventQueues
        if is_initial then
            SDL.SDL_SetMainReady()
            SDL.SDL_Init(SDL.SDL_INIT_VIDEO) |> ignore
            //SDL.SDL_SetHint(SDL.SDL_HINT_QUIT_ON_LAST_WINDOW_CLOSE, "0") |> ignore

        window <- SDL.SDL_CreateWindow(t, SDL.SDL_WINDOWPOS_CENTERED, SDL.SDL_WINDOWPOS_CENTERED,
                                viewWidth, viewHeight, windowFlags)
        renderer <- SDL.SDL_CreateRenderer(window, -1, SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED |||
                                                SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC)

        texture <- SDL.SDL_CreateTexture(renderer, SDL.SDL_PIXELFORMAT_RGBA32, SDL.SDL_TEXTUREACCESS_STREAMING, viewWidth, viewHeight)

        frameBuffer <- Array.create (viewWidth * viewHeight * 4) (byte 0)
        bufferPtr <- IntPtr ((Marshal.UnsafeAddrOfPinnedArrayElement (frameBuffer, 0)).ToPointer ())

        img <- new Image<Rgba32>(w, h, Color.Black) |> Some

        if is_initial then
            SDL.SDL_StartTextInput()
        
        windowId <- SDL.SDL_GetWindowID(window)
        eventQueues <- eventQueues.Add (windowId, Queue())
    
    member this.Cleanup () = 
        if not disposed then
            disposed <- true
            this.HideWindow ()
            SDL.SDL_DestroyTexture texture
            //printfn "Texture destroyed"
            SDL.SDL_DestroyRenderer renderer
            //printfn "Render destroyed"
            SDL.SDL_DestroyWindow window
            //printfn "Window destroyed"
            eventQueues <- eventQueues.Remove windowId

            if 0 = Map.count eventQueues then
                SDL.SDL_QuitSubSystem(SDL.SDL_INIT_VIDEO) |> ignore

            window <- IntPtr.Zero
            renderer <- IntPtr.Zero
            texture <- IntPtr.Zero
            bufferPtr <- IntPtr.Zero
            frameBuffer <- Array.create 0 (byte 0)
            event <- SDL.SDL_Event()
            img <- None
            windowId <- 0u
    
    interface IDisposable with
        member this.Dispose () =
            this.Cleanup()
            GC.SuppressFinalize(this)
    
    override this.Finalize () =
        this.Cleanup()

    member this.Clear () =
         Option.map(fun (img: Image<Rgba32>) ->
            img.Mutate(fun ctx -> ctx.Clear(background) |> ignore)
         ) img |> ignore

    member this.Render (draw : DrawingContext -> unit) =
        Option.map(fun (img: Image<Rgba32>) ->
            img.Mutate (fun ctx ->
                DrawingContext ctx |> draw
                ctx.Crop(min viewWidth img.Width, min viewHeight img.Height)
                   .Resize(
                        options = ResizeOptions(Position = AnchorPositionMode.TopLeft,
                        Size = Size(viewWidth, viewHeight),
                        Mode = ResizeMode.BoxPad)
                    )
                |> ignore
            )
            img.CopyPixelDataTo(frameBuffer)
            SDL.SDL_UpdateTexture(texture, IntPtr.Zero, bufferPtr, viewWidth * 4) |> ignore
            SDL.SDL_RenderClear(renderer) |> ignore
            SDL.SDL_RenderCopy(renderer, texture, IntPtr.Zero, IntPtr.Zero) |> ignore
            SDL.SDL_RenderPresent(renderer) |> ignore
        ) img |> ignore
        this.Clear ()
        ()

    member private this.EnqueueEvent (event: SDL.SDL_Event)  =
        if event.window.windowID = 0u
        then
            Map.iter (
                fun _ (q: Queue<SDL.SDL_Event>) -> q.Enqueue event
            ) eventQueues
        else
            match Map.tryFind event.window.windowID eventQueues with
            | Some queue -> queue.Enqueue event
            | None -> ()
    
    member private this.AssertWindowExists () =
        if Map.containsKey windowId eventQueues |> not then
            failwith "Error: You can not get events from a disposed Window."

    member this.WaitEvent (f : Func<InternalEvent, 'a>) =
        this.AssertWindowExists ()

        if eventQueues[windowId].Count = 0
        then
            if 0 = SDL.SDL_WaitEvent(&event) then
                failwith "Error: No event arrived."
            
            this.EnqueueEvent event
            this.WaitEvent f
        else
            let ev =
                eventQueues[windowId].Dequeue ()
                |> InternalEvent
            
            f.Invoke(ev)
    
    member this.PollEvents (f : Action<InternalEvent>) =
        this.AssertWindowExists ()

        while 1 = SDL.SDL_PollEvent(&event) do
            this.EnqueueEvent event

        while eventQueues[windowId].Count <> 0 do
            let ev =
                eventQueues[windowId].Dequeue ()
                |> InternalEvent
            
            f.Invoke(ev)
            
    member this.HideWindow () =
        SDL.SDL_HideWindow window

    member this.SetClearColor (r, g, b) =
        background <- fromRgb r g b


type Event =
    | Key of char
    | DownArrow
    | UpArrow
    | LeftArrow
    | RightArrow
    | Return
    | MouseButtonDown of int * int // x,y
    | MouseButtonUp of int * int // x,y
    | MouseMotion of int * int * int * int // x,y, relx, rely
    | TimerTick

type ClassifiedEvent<'e> =
    | React of 'e
    | Quit
    | Ignore

let toKeyboardEvent event =
    let (InternalEvent _event) = event
    match SDL.convertEvent _event with
    | SDL.Window wev when wev.event = SDL.SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE -> Quit
    | SDL.KeyDown kevent when kevent.keysym.sym = SDL.SDLK_ESCAPE -> Quit
    | SDL.KeyDown kevent -> React (KeyAction.KeyPress, toKeyboardKey kevent)
    | SDL.KeyUp kevent -> React (KeyAction.KeyRelease, toKeyboardKey kevent)
    | _ -> Ignore

let private classifyEvent userClassify ev =
    let (InternalEvent _ev) = ev
    match SDL.convertEvent _ev with
        | SDL.Window wev when wev.event = SDL.SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE -> Quit
        // | SDL.Window wevent->
        //     printfn "Window event %A" wevent.event
        //     // if wevent.event = SDL.SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE then Quit
        //     // else Ignore(SDL.Window wevent)
        //     Ignore(SDL.Window wevent)
        | SDL.KeyDown kevent when kevent.keysym.sym = SDL.SDLK_ESCAPE -> Quit
        | SDL.KeyDown kevent when kevent.keysym.sym = SDL.SDLK_UP -> React UpArrow
        | SDL.KeyDown kevent when kevent.keysym.sym = SDL.SDLK_DOWN -> React DownArrow
        | SDL.KeyDown kevent when kevent.keysym.sym = SDL.SDLK_LEFT -> React LeftArrow
        | SDL.KeyDown kevent when kevent.keysym.sym = SDL.SDLK_RIGHT -> React RightArrow
        | SDL.KeyDown kevent when kevent.keysym.sym = SDL.SDLK_RETURN -> React Return
        | SDL.MouseButtonDown mouseButtonEvent ->
            (mouseButtonEvent.x,mouseButtonEvent.y) |> MouseButtonDown |> React
        | SDL.MouseButtonUp mouseButtonEvent ->
            (mouseButtonEvent.x,mouseButtonEvent.y) |> MouseButtonUp |> React
        | SDL.MouseMotion motion ->
            (motion.x,motion.y,motion.xrel, motion.yrel) |> MouseMotion |> React
        | SDL.TextInput tinput -> tinput |> Key |> React
        | SDL.User uev -> userClassify uev
        | _ -> Ignore

let private userClassify : SDL.SDL_UserEvent -> ClassifiedEvent<Event> = function
    | uev when uev.``type`` = TIMER_EVENT -> React TimerTick
    | _ -> Ignore

let private timer interval =
    let ticker _ = timerTickEvent () 
    let timer = new System.Timers.Timer(float interval)
    timer.AutoReset <- true
    timer.Elapsed.Add ticker
    timer.Start()
    Some timer

let runAppWithTimer (t:string) (w:int) (h:int) (interval:int option)
        (draw: 's -> drawing_fun)
        (react: 's -> Event -> 's option) (s: 's) : unit =
    let window = new Window(t, w, h)

    let mutable state = s
    let rec drawLoop redraw =
        if redraw then
            window.Render (draw state >> ignore)
        match window.WaitEvent (classifyEvent userClassify) with
            | Quit ->
                // printfn "We quit"
                window.HideWindow ()
                () // quit the interaction by exiting the loop
            | React ev ->
                let redraw =
                    match react state ev with
                        | Some s' -> state <- s'; true
                        | None   -> false
                drawLoop redraw
            | Ignore ->
                // printfn "We loop because of: %A" sdlEvent
                drawLoop false
    let timer = Option.bind (fun t -> timer t) interval
    
    drawLoop true
    timer |> Option.map (fun timer -> timer.Stop()) |> ignore
    //printfn "Out of loop"

let runApp (t:string) (w:int) (h:int) (draw: unit -> drawing_fun) : unit =
    let drawWState s = draw ()
    let react s e = None
    runAppWithTimer t w h None drawWState react 0
