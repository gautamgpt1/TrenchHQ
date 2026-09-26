use serde::Deserialize;
use trenchhq_onchain_engine::protocol::{uniswap_v2, uniswap_v3, uniswap_v4};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Fixture {
    collected_at_utc: String,
    source_url: String,
    v2: V2Fixture,
    v3: V3Fixture,
    v4: V4Fixture,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct V2Fixture {
    chain_id: String,
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
    pool: String,
    token0: String,
    token1: String,
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
struct V4Fixture {
    chain_id: String,
    pool_manager: String,
    state_view: String,
    pool_id: String,
    currency0: String,
    currency1: String,
    source_urls: Vec<String>,
    swap_block_number: String,
    swap_block_hash: String,
    swap_parent_hash: String,
    swap_timestamp: String,
    swap_transaction_hash: String,
    transaction_status: String,
    swap: RawLog,
    snapshot_block_number: String,
    snapshot_block_hash: String,
    snapshot_parent_hash: String,
    snapshot_timestamp: String,
    slot0: String,
    liquidity: String,
    expected_last_trade_quote_per_base_coefficient: String,
    expected_event_spot_quote_per_base_coefficient: String,
    expected_snapshot_spot_quote_per_base_coefficient: String,
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
    serde_json::from_str(include_str!(
        "fixtures/ethereum-mainnet-uniswap-20260903.json"
    ))
    .expect("Ethereum mainnet fixture must remain valid JSON")
}

#[test]
fn uniswap_v2_mainnet_swap_sync_and_snapshot_match() {
    let fixture = fixture();
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
    assert!(!fixture.collected_at_utc.is_empty());
    assert_eq!(fixture.source_url, "https://eth.drpc.org");

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
        uniswap_v2::spot_price(&snapshot, false, 18, 6)
            .unwrap()
            .coefficient,
        v2.expected_spot_quote_per_base_coefficient
    );

    let swap = uniswap_v2::decode_swap(&swap_log.topics, &swap_log.data).unwrap();
    assert_eq!(
        uniswap_v2::executed_price(&swap, false, 18, 6)
            .unwrap()
            .coefficient,
        v2.expected_last_trade_quote_per_base_coefficient
    );
}

#[test]
fn uniswap_v3_mainnet_swap_and_pinned_state_match() {
    let fixture = fixture();
    let v3 = fixture.v3;
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
    assert_eq!(v3.swap.transaction_index, "0x5b");
    assert_eq!(v3.swap.log_index, "0x7b");

    let swap = uniswap_v3::decode_swap(&v3.swap.topics, &v3.swap.data).unwrap();
    let slot0 = uniswap_v3::decode_slot0(&v3.slot0).unwrap();
    let liquidity = uniswap_v3::decode_liquidity(&v3.liquidity).unwrap();
    assert_eq!(swap.sqrt_price_x96, slot0.sqrt_price_x96);
    assert_eq!(swap.tick, slot0.tick);
    assert_eq!(swap.liquidity, liquidity);
    assert_eq!(
        uniswap_v3::executed_price(&swap, false, 18, 6)
            .unwrap()
            .coefficient,
        v3.expected_last_trade_quote_per_base_coefficient
    );
    assert_eq!(
        uniswap_v3::spot_price(&slot0.sqrt_price_x96, false, 18, 6)
            .unwrap()
            .coefficient,
        v3.expected_spot_quote_per_base_coefficient
    );
}

#[test]
fn uniswap_v4_mainnet_pool_manager_swap_and_state_view_match() {
    let fixture = fixture();
    let v4 = fixture.v4;
    assert_eq!(v4.chain_id, "1");
    assert_eq!(v4.pool_manager, uniswap_v4::MAINNET_POOL_MANAGER);
    assert!(uniswap_v2::is_address(&v4.state_view));
    assert!(uniswap_v4::is_pool_id(&v4.pool_id));
    assert!(uniswap_v2::is_address(&v4.currency0));
    assert!(uniswap_v2::is_address(&v4.currency1));
    assert_eq!(v4.source_urls.len(), 2);
    assert!(v4.swap_block_number.starts_with("0x"));
    assert_eq!(v4.swap_block_hash.len(), 66);
    assert_eq!(v4.swap_parent_hash.len(), 66);
    assert!(v4.swap_timestamp.starts_with("0x"));
    assert_eq!(v4.swap_transaction_hash.len(), 66);
    assert_eq!(v4.transaction_status, "0x1");
    assert_eq!(v4.swap.transaction_index, "0x113");
    assert_eq!(v4.swap.log_index, "0x360");
    assert!(v4.snapshot_block_number.starts_with("0x"));
    assert_eq!(v4.snapshot_block_hash.len(), 66);
    assert_eq!(v4.snapshot_parent_hash.len(), 66);
    assert!(v4.snapshot_timestamp.starts_with("0x"));

    let swap = uniswap_v4::decode_swap(&v4.swap.topics, &v4.swap.data).unwrap();
    assert_eq!(swap.pool_id, v4.pool_id);
    assert_eq!(
        uniswap_v4::executed_price(&swap, false, 18, 18)
            .unwrap()
            .coefficient,
        v4.expected_last_trade_quote_per_base_coefficient
    );
    assert_eq!(
        uniswap_v4::spot_price(&swap.sqrt_price_x96, false, 18, 18)
            .unwrap()
            .coefficient,
        v4.expected_event_spot_quote_per_base_coefficient
    );

    let slot0 = uniswap_v4::decode_slot0(&v4.slot0).unwrap();
    uniswap_v4::decode_liquidity(&v4.liquidity).unwrap();
    assert_eq!(
        uniswap_v4::spot_price(&slot0.sqrt_price_x96, false, 18, 18)
            .unwrap()
            .coefficient,
        v4.expected_snapshot_spot_quote_per_base_coefficient
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
    assert_eq!(chain_id, "1");
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
