use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
    println!("cargo:rerun-if-changed=include/quiche.h");
    // Generated C# is a platform-specific reference. Keep the portable managed
    // bindings checked in; a native build must never overwrite them.
    // using bindgen, generate binding code
    bindgen::Builder::default()
        .header("include/quiche.h")
        .generate()?
        .write_to_file("src/quiche.rs")?;

    // csbindgen code, generate C# dll import
    csbindgen::Builder::default()
        .input_bindgen_file("src/quiche.rs")
        .method_filter(|x| x.starts_with("quiche_"))
        .rust_file_header("extern crate quiche;\nuse super::quiche::*;")
        .rust_method_prefix("_")
        .csharp_entry_point_prefix("_")
        .csharp_dll_name("quiche_bindgen")
        .csharp_namespace("Cloudflare.Quiche")
        .generate_to_file(
            "src/quiche_ffi.rs",
            &format!("{}/NativeMethods.g.cs", std::env::var("OUT_DIR")?),
        )
        .unwrap();

    Ok(())
}
