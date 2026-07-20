using System.Runtime.InteropServices;

namespace ED8Editor.Decompiler;

/// <summary>
/// Declarations P/Invoke vers cs1_decompiler.dll (moteur natif valide, C ABI).
/// Toutes les fonctions sont extern "C" __cdecl. Les valeurs C++ `long` sont
/// 32 bits sous Windows (MSVC et MinGW/LLP64) : marshalees en <see cref="int"/>.
/// </summary>
internal static class NativeMethods
{
    private const string Dll = "cs1_decompiler";
    private const CallingConvention Cc = CallingConvention.Cdecl;

    // ---- registre (charge une seule fois le document cs1_instructions.json) ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_load_registry(byte[] jsonUtf8);

    // ---- document ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_open(byte[] data, int len, [MarshalAs(UnmanagedType.LPStr)] string filename);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern void cs1i_close(IntPtr doc);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_scene_name(IntPtr doc);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_func_count(IntPtr doc);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_func_name(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_func_is_code(IntPtr doc, int f);

    // ---- instructions ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_func_ninstr(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_instr_name(IntPtr doc, int f, int k);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_instr_reg(IntPtr doc, int f, int k);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_instr_op(IntPtr doc, int f, int k);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_instr_argc(IntPtr doc, int f, int k);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_instr_argtype(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_instr_argi(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern double cs1i_instr_argf(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_instr_argbytes(IntPtr doc, int f, int k, int a, byte[]? outBuf, int cap);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_instr_offset(IntPtr doc, int f, int k);

    // ---- expressions ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_is_expr(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_expr_count(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_expr_subop(IntPtr doc, int f, int k, int a, int i);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_expr_kind(IntPtr doc, int f, int k, int a, int i);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_expr_value(IntPtr doc, int f, int k, int a, int i);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_expr_elem_label(IntPtr doc, int f, int k, int a, int i);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_expr_nested_name(IntPtr doc, int f, int k, int a, int i);

    // ---- registre : introspection (facultatif) ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_reg_count();
}
