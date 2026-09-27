use serde::Deserialize;
use std::collections::HashSet;
use trenchhq_onchain_engine::protocol::{
    aerodrome_classic, aerodrome_slipstream, uniswap_v2, uniswap_v4,
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Fixture {
    collected_at_utc: String,
    source_url: String,
    v4: V4Fixture,
    aerodrome_classic: Vec<AerodromeClassicFixture>,
    aerodrome_slipstream: Vec<AerodromeSlipstreamFixture>,
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
    fee: u32,
    tick_spacing: i32,
    hook_address: String,
    initialize_block_number: String,
    initialize_block_hash: String,
    initialize_parent_hash: String,
    initialize_timestamp: String,
    initialize_transaction_hash: String,
    initialize_transaction_status: String,
    initialize: RawLog,
    swap_block_number: String,
    swap_block_hash: String,
    swap_parent_hash: String,
    swap_timestamp: String,
    swap_transaction_hash: String,
    swap_transaction_status: String,
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

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct AerodromeClassicFixture {
    family: String,
    chain_id: String,
    factory: String,
    pool: String,
    token0: String,
    token1: String,
    factory_identity: String,
    factory_is_pool: String,
    reverse_pool: String,
    stable: bool,
    stable_state: String,
    block_number: String,
    block_hash: String,
    parent_hash: String,
    timestamp: String,
    transaction_hash: String,
    transaction_status: String,
    sync: RawLog,
    swap: RawLog,
    reserves: String,
    expected_last_trade_quote_per_base_coefficient: String,
    expected_spot_quote_per_base_coefficient: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct AerodromeSlipstreamFixture {
    family: String,
    chain_id: String,
    factory: String,
    pool: String,
    token0: String,
    token1: String,
    factory_identity: String,
    factory_is_pool: String,
    reverse_pool: String,
    tick_spacing: i32,
    tick_spacing_state: String,
    dynamic_fee: String,
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

fn fixture() -> Fixture {
    serde_json::from_str(include_str!(
        "fixtures/base-mainnet-v4-aerodrome-20260904.json"
    ))
    .expect("Base V4 and Aerodrome fixture must remain valid JSON")
}

#[test]
fn uniswap_v4_base_zero_hook_swap_and_state_match() {
    let fixture = fixture();
    assert!(!fixture.collected_at_utc.is_empty());
    assert_eq!(fixture.source_url, "https://mainnet.base.org");
    let v4 = fixture.v4;
    assert_eq!(v4.chain_id, "8453");
    for address in [
        &v4.pool_manager,
        &v4.state_view,
        &v4.currency0,
        &v4.currency1,
        &v4.hook_address,
    ] {
        assert!(uniswap_v2::is_address(address));
    }
    assert_eq!(
        v4.pool_manager,
        "0x498581ff718922c3f8e6a244956af099b2652b2b"
    );
    assert_eq!(v4.fee, 3000);
    assert_eq!(v4.tick_spacing, 60);
    assert_eq!(
        v4.hook_address,
        "0x0000000000000000000000000000000000000000"
    );
    assert!(uniswap_v4::is_pool_id(&v4.pool_id));
    assert_provenance(
        &v4.initialize_block_number,
        &v4.initialize_block_hash,
        &v4.initialize_parent_hash,
        &v4.initialize_timestamp,
        &v4.initialize_transaction_hash,
        &v4.initialize_transaction_status,
    );
    assert_eq!(v4.initialize.topics[1], v4.pool_id);
    assert!(v4.initialize.data.starts_with("0x"));
    assert!(v4.initialize.transaction_index.starts_with("0x"));
    assert!(v4.initialize.log_index.starts_with("0x"));
    assert_provenance(
        &v4.swap_block_number,
        &v4.swap_block_hash,
        &v4.swap_parent_hash,
        &v4.swap_timestamp,
        &v4.swap_transaction_hash,
        &v4.swap_transaction_status,
    );
    assert!(v4.swap.transaction_index.starts_with("0x"));
    assert!(v4.swap.log_index.starts_with("0x"));
    let swap = uniswap_v4::decode_swap(&v4.swap.topics, &v4.swap.data).unwrap();
    assert_eq!(swap.pool_id, v4.pool_id);
    assert_eq!(
        uniswap_v4::executed_price(&swap, true, 18, 6)
            .unwrap()
            .coefficient,
        v4.expected_last_trade_quote_per_base_coefficient
    );
    let slot0 = uniswap_v4::decode_slot0(&v4.slot0).unwrap();
    uniswap_v4::decode_liquidity(&v4.liquidity).unwrap();
    assert_eq!(swap.sqrt_price_x96, slot0.sqrt_price_x96);
    assert_eq!(swap.tick, slot0.tick);
    assert_eq!(
        uniswap_v4::spot_price(&slot0.sqrt_price_x96, true, 18, 6)
            .unwrap()
            .coefficient,
        v4.expected_spot_quote_per_base_coefficient
    );
}

#[test]
fn aerodrome_classic_volatile_and_stable_fixtures_match() {
    let fixture = fixture();
    assert_eq!(fixture.aerodrome_classic.len(), 2);
    for item in fixture.aerodrome_classic {
        assert_eq!(item.chain_id, "8453");
        assert_eq!(item.factory, aerodrome_classic::BASE_FACTORY);
        assert_eq!(item.factory_identity, item.factory);
        assert_eq!(item.reverse_pool, item.pool);
        assert_eq!(item.factory_is_pool, bool_word(true));
        assert_eq!(item.stable_state, bool_word(item.stable));
        assert!(uniswap_v2::is_address(&item.pool));
        assert!(uniswap_v2::is_address(&item.token0));
        assert!(uniswap_v2::is_address(&item.token1));
        assert_ne!(item.token0, item.token1);
        assert_provenance(
            &item.block_number,
            &item.block_hash,
            &item.parent_hash,
            &item.timestamp,
            &item.transaction_hash,
            &item.transaction_status,
        );
        assert!(item.sync.transaction_index.starts_with("0x"));
        assert!(item.sync.log_index.starts_with("0x"));
        assert!(item.swap.transaction_index.starts_with("0x"));
        assert!(item.swap.log_index.starts_with("0x"));
        let swap = aerodrome_classic::decode_swap(&item.swap.topics, &item.swap.data).unwrap();
        assert_eq!(
            aerodrome_classic::executed_price(&swap, true, 18, 6)
                .unwrap()
                .coefficient,
            item.expected_last_trade_quote_per_base_coefficient
        );
        let sync = aerodrome_classic::decode_sync(&item.sync.data).unwrap();
        let reserves = aerodrome_classic::decode_reserves(&item.reserves).unwrap();
        assert_eq!(sync, reserves);
        let spot = if item.stable {
            assert_eq!(item.family, "classic-stable");
            aerodrome_classic::stable_spot_price(&reserves, true, 18, 6).unwrap()
        } else {
            assert_eq!(item.family, "classic-volatile");
            aerodrome_classic::volatile_spot_price(&reserves, true, 18, 6).unwrap()
        };
        assert_eq!(
            spot.coefficient,
            item.expected_spot_quote_per_base_coefficient
        );
    }
}

#[test]
fn every_allowlisted_slipstream_generation_matches_its_fixture() {
    let fixture = fixture();
    assert_eq!(fixture.aerodrome_slipstream.len(), 3);
    let expected_factories = HashSet::from([
        aerodrome_slipstream::BASE_INITIAL_FACTORY,
        aerodrome_slipstream::BASE_GAUGE_CAPS_FACTORY,
        aerodrome_slipstream::BASE_GAUGES_V3_FACTORY,
    ]);
    let actual_factories = fixture
        .aerodrome_slipstream
        .iter()
        .map(|item| item.factory.as_str())
        .collect::<HashSet<_>>();
    assert_eq!(actual_factories, expected_factories);

    for item in fixture.aerodrome_slipstream {
        assert_eq!(item.chain_id, "8453");
        assert!(item.family.starts_with("slipstream-"));
        assert_eq!(item.factory_identity, item.factory);
        assert_eq!(item.reverse_pool, item.pool);
        assert_eq!(item.factory_is_pool, bool_word(true));
        assert!(uniswap_v2::is_address(&item.pool));
        assert!(uniswap_v2::is_address(&item.token0));
        assert!(uniswap_v2::is_address(&item.token1));
        assert_ne!(item.token0, item.token1);
        assert!(item.tick_spacing > 0);
        assert_eq!(item.tick_spacing_state, uint_word(item.tick_spacing as u32));
        assert_ne!(item.dynamic_fee, uint_word(0));
        assert_provenance(
            &item.block_number,
            &item.block_hash,
            &item.parent_hash,
            &item.timestamp,
            &item.transaction_hash,
            &item.transaction_status,
        );
        assert!(item.swap.transaction_index.starts_with("0x"));
        assert!(item.swap.log_index.starts_with("0x"));
        let swap = aerodrome_slipstream::decode_swap(&item.swap.topics, &item.swap.data).unwrap();
        assert_eq!(
            aerodrome_slipstream::executed_price(&swap, true, 18, 6)
                .unwrap()
                .coefficient,
            item.expected_last_trade_quote_per_base_coefficient
        );
        let slot0 = aerodrome_slipstream::decode_slot0(&item.slot0).unwrap();
        aerodrome_slipstream::decode_liquidity(&item.liquidity).unwrap();
        assert_eq!(slot0.sqrt_price_x96, swap.sqrt_price_x96);
        assert_eq!(slot0.tick, swap.tick);
        assert_eq!(
            aerodrome_slipstream::spot_price(&slot0.sqrt_price_x96, true, 18, 6)
                .unwrap()
                .coefficient,
            item.expected_spot_quote_per_base_coefficient
        );
    }
}

fn bool_word(value: bool) -> String {
    uint_word(u32::from(value))
}

fn uint_word(value: u32) -> String {
    format!("0x{value:064x}")
}

fn assert_provenance(
    block_number: &str,
    block_hash: &str,
    parent_hash: &str,
    timestamp: &str,
    transaction_hash: &str,
    transaction_status: &str,
) {
    assert!(block_number.starts_with("0x"));
    assert_eq!(block_hash.len(), 66);
    assert_eq!(parent_hash.len(), 66);
    assert!(timestamp.starts_with("0x"));
    assert_eq!(transaction_hash.len(), 66);
    assert_eq!(transaction_status, "0x1");
}
