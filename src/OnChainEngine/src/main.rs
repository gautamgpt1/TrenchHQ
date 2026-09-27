fn main() {
    let stdin = std::io::stdin();
    let stdout = std::io::stdout();

    if let Err(error) = trenchhq_onchain_engine::run(stdin.lock(), stdout.lock()) {
        eprintln!("on-chain engine stopped: {error}");
        std::process::exit(1);
    }
}
