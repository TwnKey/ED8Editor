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

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_load_tables_schema(byte[] jsonUtf8);

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

    // ---- tables de donnees ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_func_is_table(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_table_kind(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_id(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_is_stale(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_field_count(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_table_field_type(IntPtr doc, int f, int j);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_field_i(IntPtr doc, int f, int j);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern double cs1i_table_field_f(IntPtr doc, int f, int j);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_table_field_text(IntPtr doc, int f, int j);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_field_bytes(IntPtr doc, int f, int j, byte[]? outBuf, int cap);

    // ---- edition de champs de table (scalaire/f32/string-largeur-fixe = taille preservee) ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_set_field_i(IntPtr doc, int f, int j, int v);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_set_field_f(IntPtr doc, int f, int j, double v);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_set_field_text(IntPtr doc, int f, int j, [MarshalAs(UnmanagedType.LPStr)] string s);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_set_field_bytes(IntPtr doc, int f, int j, byte[] bytes, int n);

    // ---- ajout / suppression de tables entieres (change le nombre de fonctions) ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_func_remove(IntPtr doc, int f);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_add(IntPtr doc, int pos, [MarshalAs(UnmanagedType.LPStr)] string name, byte[] bytes, int len);

    // ---- lignes de table : champs (insert/delete) + schema du record ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_field_insert(IntPtr doc, int f, int at, [MarshalAs(UnmanagedType.LPStr)] string type, int size);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_table_field_delete(IntPtr doc, int f, int j);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_schema_record_len([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_schema_field_count([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_schema_field_type([MarshalAs(UnmanagedType.LPStr)] string name, int j);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_schema_field_size([MarshalAs(UnmanagedType.LPStr)] string name, int j);

    // ---- iterations de boucle (kind==7 ; count auto-synchronise) ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_is_loop(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_loop_count(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_loop_dup(IntPtr doc, int f, int k, int a, int it);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_loop_remove(IntPtr doc, int f, int k, int a, int it);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_loop_elem_argc(IntPtr doc, int f, int k, int a, int it);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_loop_elem_i(IntPtr doc, int f, int k, int a, int it, int e);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_loop_set_elem_i(IntPtr doc, int f, int k, int a, int it, int e, int v);

    // ---- construction d'expression ----
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_expr_clear(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_arg_expr_push(IntPtr doc, int f, int k, int a, int subop, int value);

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

    // annotations semantiques de l'operande (name / sem / sem_arg / sem_span)
    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_instr_argname(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_instr_argsem(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern IntPtr cs1i_instr_argsem_arg(IntPtr doc, int f, int k, int a);

    [DllImport(Dll, CallingConvention = Cc)]
    public static extern int cs1i_instr_argsem_span(IntPtr doc, int f, int k, int a);

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
