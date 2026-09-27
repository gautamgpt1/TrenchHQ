use std::{env, path::PathBuf, process::Command};

fn main() {
    println!("cargo:rerun-if-changed=../TrenchHQ.App/Package.appxmanifest");
    println!("cargo:rerun-if-changed=../../Scripts/Build-VersionResource.ps1");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows")
        || env::var("CARGO_CFG_TARGET_ENV").as_deref() != Ok("msvc")
    {
        return;
    }

    let resource =
        PathBuf::from(env::var_os("OUT_DIR").expect("Cargo OUT_DIR")).join("TrenchHQ.Version.res");
    let status = Command::new("powershell.exe")
        .args([
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            "../../Scripts/Build-VersionResource.ps1",
            "-OutputPath",
        ])
        .arg(&resource)
        .status()
        .expect("Windows version-resource compiler");
    assert!(
        status.success(),
        "Windows version-resource compilation failed"
    );
    println!(
        "cargo:rustc-link-arg-bin=trenchhq-onchain-engine={}",
        resource.display()
    );
}
