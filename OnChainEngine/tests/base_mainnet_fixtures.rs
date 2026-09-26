use serde::Deserialize;
use trenchhq_onchain_engine::protocol::{uniswap_v2, uniswap_v3};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Fixture {
    collected_at_utc: String,
    source_url: String,
    v2: V2Fixture,
    v3: V3Fixture,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct V2Fixture {
    chain_id: String,
    factory: String,
    pool: String,
    token0: String,
    token1: String,
    block_number: String,
    block_hash: String,
    parent_hash: String,
    timestamp: String,
    transaction_hash: String,
    transaction_status: String,
    logs: Vec<RawLog>,
    get_reserves: String,
    expected_last_trade_quote_per_base_coefficient: String,
    expected_spot_quote_per_base_coefficient: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct V3Fixture {
    chain_id: String,
    factory: String,
    pool: String,
    token0: String,
    token1: String,
    fee: u32,
    tick_spacing: i32,
    block_number: String,
    block_hash: String,
    parent_hash: String,
    timestamp: String,
    transaction_hash: String,
    transaction_status: String,
    swap: RawLog,
    slot0: String,
    liquidity: String,
    expected_last_trade_quote_per_base_coefficient: String,
    expected_spot_quote_per_base_coefficient: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawLog {
    topics: Vec<String>,
    data: String,
    transaction_index: String,
    log_index: String,
}

fn fixture() -> Fixture {
    serde_json::from_str(include_str!("fixtures/base-mainnet-uniswap-20260904.json"))
        .expect("Base mainnet fixture must remain valid JSON")
}

#[test]
fn uniswap_v2_base_swap_sync_and_snapshot_match() {
    let fixture = fixture();
    assert!(!fixture.collected_at_utc.is_empty());
    assert_eq!(fixture.source_url, "https://mainnet.base.org");
    let v2 = fixture.v2;
    assert_provenance(
        &v2.chain_id,
        &v2.pool,
        &v2.token0,
        &v2.token1,
        &v2.block_number,
        &v2.block_hash,
        &v2.parent_hash,
        &v2.timestamp,
        &v2.transaction_hash,
        &v2.transaction_status,
    );
    assert_eq!(v2.factory, "0x8909dc15e40173ff4699343b6eb8132c65e18ec6");
    let sync_log = v2
        .logs
        .iter()
        .find(|log| log.topics[0].eq_ignore_ascii_case(uniswap_v2::SYNC_TOPIC))
        .expect("fixture must contain the pair Sync log");
    let swap_log = v2
        .logs
        .iter()
        .find(|log| log.topics[0].eq_ignore_ascii_case(uniswap_v2::SWAP_TOPIC))
        .expect("fixture must contain the pair Swap log");
    assert_eq!(sync_log.transaction_index, swap_log.transaction_index);
    assert_ne!(sync_log.log_index, swap_log.log_index);
    let sync = uniswap_v2::decode_sync(&sync_log.data).unwrap();
    let snapshot = uniswap_v2::decode_reserves(&v2.get_reserves).unwrap();
    assert_eq!(sync, snapshot);
    assert_eq!(
        uniswap_v2::spot_price(&snapshot, true, 18, 6)
            .unwrap()
            .coefficient,
        v2.expected_spot_quote_per_base_coefficient
    );
    let swap = uniswap_v2::decode_swap(&swap_log.topics, &swap_log.data).unwrap();
    assert_eq!(
        uniswap_v2::executed_price(&swap, true, 18, 6)
            .unwrap()
            .coefficient,
        v2.expected_last_trade_quote_per_base_coefficient
    );
}

#[test]
fn uniswap_v3_base_swap_and_pinned_state_match() {
    let v3 = fixture().v3;
    assert_provenance(
        &v3.chain_id,
        &v3.pool,
        &v3.token0,
        &v3.token1,
        &v3.block_number,
        &v3.block_hash,
        &v3.parent_hash,
        &v3.timestamp,
        &v3.transaction_hash,
        &v3.transaction_status,
    );
    assert_eq!(v3.factory, "0x33128a8fc17869897dce68ed026d694621f6fdfd");
    assert_eq!(v3.fee, 3000);
    assert_eq!(v3.tick_spacing, 60);
    assert!(v3.swap.transaction_index.starts_with("0x"));
    assert!(v3.swap.log_index.starts_with("0x"));
    let swap = uniswap_v3::decode_swap(&v3.swap.topics, &v3.swap.data).unwrap();
    let slot0 = uniswap_v3::decode_slot0(&v3.slot0).unwrap();
    let liquidity = uniswap_v3::decode_liquidity(&v3.liquidity).unwrap();
    assert_eq!(swap.sqrt_price_x96, slot0.sqrt_price_x96);
    assert_eq!(swap.tick, slot0.tick);
    assert_eq!(swap.liquidity, liquidity);
    assert_eq!(
        uniswap_v3::executed_price(&swap, true, 18, 6)
            .unwrap()
            .coefficient,
        v3.expected_last_trade_quote_per_base_coefficient
    );
    assert_eq!(
        uniswap_v3::spot_price(&slot0.sqrt_price_x96, true, 18, 6)
            .unwrap()
            .coefficient,
        v3.expected_spot_quote_per_base_coefficient
    );
}

#[allow(clippy::too_many_arguments)]
fn assert_provenance(
    chain_id: &str,
    pool: &str,
    token0: &str,
    token1: &str,
    block_number: &str,
    block_hash: &str,
    parent_hash: &str,
    timestamp: &str,
    transaction_hash: &str,
    transaction_status: &str,
) {
    assert_eq!(chain_id, "8453");
    for address in [pool, token0, token1] {
        assert!(uniswap_v2::is_address(address));
    }
    assert!(block_number.starts_with("0x"));
    assert_eq!(block_hash.len(), 66);
    assert_eq!(parent_hash.len(), 66);
    assert!(timestamp.starts_with("0x"));
    assert_eq!(transaction_hash.len(), 66);
    assert_eq!(transaction_status, "0x1");
}
