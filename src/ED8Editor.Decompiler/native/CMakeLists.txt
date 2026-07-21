cmake_minimum_required(VERSION 3.15)
project(cs1_decompiler CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(cs1_decompiler SHARED cs1_instr_api.cpp)

if (MSVC)
  target_compile_definitions(cs1_decompiler PRIVATE _CRT_SECURE_NO_WARNINGS)
  target_compile_options(cs1_decompiler PRIVATE /utf-8 /O2 /EHsc)
endif()

# Sortie : cs1_decompiler.dll (a copier a cote de l'executable .NET,
# ou laisser le csproj le recopier depuis ce dossier).
