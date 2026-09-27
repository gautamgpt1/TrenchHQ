use crate::protocol::{
    aerodrome_classic, aerodrome_slipstream, curve, fermi_swap, pancake_infinity_bin,
    pancake_infinity_cl, pancake_v2, pancake_v3, pons_v2_curve, uniswap_v2, uniswap_v3, uniswap_v4,
};

pub const CHAIN_NAMESPACE: &str = "eip155";

pub struct StableAsset {
    pub symbol: &'static str,
    pub address: &'static str,
}

pub struct EvmChainDefinition {
    pub chain_id: &'static str,
    pub source_namespace: &'static str,
    pub native_asset_address: &'static str,
    pub wrapped_native_asset_address: &'static str,
    pub native_usd_reference_pool: &'static str,
    pub native_usd_reference_protocol: &'static str,
    pub native_usd_reference_source: &'static str,
    pub reference_price_id: &'static str,
    pub stable_assets: &'static [StableAsset],
    pub uniswap_v2_deployments: &'static [&'static str],
    pub pancake_v2_deployments: &'static [&'static str],
    pub uniswap_v3_deployments: &'static [&'static str],
    pub uniswap_v4_deployments: &'static [&'static str],
    pub pancake_v3_deployments: &'static [&'static str],
    pub pancake_infinity_cl_deployments: &'static [&'static str],
    pub pancake_infinity_bin_deployments: &'static [&'static str],
    pub aerodrome_classic_deployments: &'static [&'static str],
    pub aerodrome_slipstream_deployments: &'static [&'static str],
    pub curve_deployments: &'static [&'static str],
    pub fermi_swap_deployments: &'static [&'static str],
}

impl EvmChainDefinition {
    pub fn stable_symbol(&self, address: &str) -> Option<&'static str> {
        self.stable_assets
            .iter()
            .find(|asset| address.eq_ignore_ascii_case(asset.address))
            .map(|asset| asset.symbol)
    }

    pub fn supports_deployment(&self, protocol_id: &str, address: &str) -> bool {
        if protocol_id == pons_v2_curve::PROTOCOL_ID {
            return self.chain_id == "4663"
                && address.eq_ignore_ascii_case(pons_v2_curve::ROBINHOOD_FACTORY);
        }
        let deployments = match protocol_id {
            uniswap_v2::PROTOCOL_ID => self.uniswap_v2_deployments,
            pancake_v2::PROTOCOL_ID => self.pancake_v2_deployments,
            uniswap_v3::PROTOCOL_ID => self.uniswap_v3_deployments,
            uniswap_v4::PROTOCOL_ID => self.uniswap_v4_deployments,
            pancake_v3::PROTOCOL_ID => self.pancake_v3_deployments,
            pancake_infinity_cl::PROTOCOL_ID => self.pancake_infinity_cl_deployments,
            pancake_infinity_bin::PROTOCOL_ID => self.pancake_infinity_bin_deployments,
            aerodrome_classic::PROTOCOL_ID => self.aerodrome_classic_deployments,
            aerodrome_slipstream::PROTOCOL_ID => self.aerodrome_slipstream_deployments,
            curve::PROTOCOL_ID => self.curve_deployments,
            fermi_swap::PROTOCOL_ID => self.fermi_swap_deployments,
            _ => return false,
        };
        deployments
            .iter()
            .any(|deployment| address.eq_ignore_ascii_case(deployment))
    }

    pub fn supports_slipstream_tick_spacing(&self, factory: &str, tick_spacing: i32) -> bool {
        if self.chain_id != "8453" {
            return false;
        }
        let allowed = if factory.eq_ignore_ascii_case(aerodrome_slipstream::BASE_INITIAL_FACTORY) {
            &[1, 10, 50, 100, 200, 2000][..]
        } else if factory.eq_ignore_ascii_case(aerodrome_slipstream::BASE_GAUGE_CAPS_FACTORY)
            || factory.eq_ignore_ascii_case(aerodrome_slipstream::BASE_GAUGES_V3_FACTORY)
        {
            &[1, 10, 50, 100, 200, 500, 2000][..]
        } else {
            return false;
        };
        allowed.contains(&tick_spacing)
    }
}

const ETHEREUM_STABLE_ASSETS: &[StableAsset] = &[
    StableAsset {
        symbol: "USDC",
        address: "0xa0b86991c6218b36c1d19d4a2e9eb0ce3606eb48",
    },
    StableAsset {
        symbol: "USDT",
        address: "0xdac17f958d2ee523a2206206994597c13d831ec7",
    },
    StableAsset {
        symbol: "DAI",
        address: "0x6b175474e89094c44da98b954eedeac495271d0f",
    },
];

const ETHEREUM_UNISWAP_V2_DEPLOYMENTS: &[&str] = &[
    uniswap_v2::MAINNET_FACTORY,
    uniswap_v2::SHIBASWAP_V1_FACTORY,
];
const ETHEREUM_PANCAKE_V2_DEPLOYMENTS: &[&str] = &[];
const ETHEREUM_UNISWAP_V3_DEPLOYMENTS: &[&str] = &[
    uniswap_v3::MAINNET_FACTORY,
    uniswap_v3::SHIBASWAP_V2_FACTORY,
];
const ETHEREUM_UNISWAP_V4_DEPLOYMENTS: &[&str] = &[uniswap_v4::MAINNET_POOL_MANAGER];
const ETHEREUM_PANCAKE_V3_DEPLOYMENTS: &[&str] = &[];
const ETHEREUM_PANCAKE_INFINITY_CL_DEPLOYMENTS: &[&str] = &[];
const ETHEREUM_PANCAKE_INFINITY_BIN_DEPLOYMENTS: &[&str] = &[];
const ETHEREUM_AERODROME_CLASSIC_DEPLOYMENTS: &[&str] = &[];
const ETHEREUM_AERODROME_SLIPSTREAM_DEPLOYMENTS: &[&str] = &[];
const ETHEREUM_CURVE_DEPLOYMENTS: &[&str] = &[curve::MAINNET_ADDRESS_PROVIDER];
const ETHEREUM_FERMI_SWAP_DEPLOYMENTS: &[&str] =
    &[fermi_swap::LEGACY_SWAPPER, fermi_swap::CURRENT_SWAPPER];

const BASE_STABLE_ASSETS: &[StableAsset] = &[StableAsset {
    symbol: "USDC",
    address: "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913",
}];
const BASE_UNISWAP_V2_DEPLOYMENTS: &[&str] = &["0x8909dc15e40173ff4699343b6eb8132c65e18ec6"];
const BASE_PANCAKE_V2_DEPLOYMENTS: &[&str] = &["0x02a84c1b3bbd7401a5f7fa98a384ebc70bb5749e"];
const BASE_UNISWAP_V3_DEPLOYMENTS: &[&str] = &["0x33128a8fc17869897dce68ed026d694621f6fdfd"];
const BASE_UNISWAP_V4_DEPLOYMENTS: &[&str] = &["0x498581ff718922c3f8e6a244956af099b2652b2b"];
const BASE_PANCAKE_V3_DEPLOYMENTS: &[&str] = &["0x0bfbcf9fa4f9c56b0f40a671ad40e0805a091865"];
const BASE_PANCAKE_INFINITY_CL_DEPLOYMENTS: &[&str] =
    &["0xa0ffb9c1ce1fe56963b0321b32e7a0302114058b"];
const BASE_PANCAKE_INFINITY_BIN_DEPLOYMENTS: &[&str] =
    &["0xc697d2898e0d09264376196696c51d7abbbaa4a9"];
const BASE_AERODROME_CLASSIC_DEPLOYMENTS: &[&str] = &[aerodrome_classic::BASE_FACTORY];
const BASE_AERODROME_SLIPSTREAM_DEPLOYMENTS: &[&str] = &[
    aerodrome_slipstream::BASE_INITIAL_FACTORY,
    aerodrome_slipstream::BASE_GAUGE_CAPS_FACTORY,
    aerodrome_slipstream::BASE_GAUGES_V3_FACTORY,
];
const BASE_CURVE_DEPLOYMENTS: &[&str] = &[];
const BASE_FERMI_SWAP_DEPLOYMENTS: &[&str] = &[];

pub static ETHEREUM_MAINNET: EvmChainDefinition = EvmChainDefinition {
    chain_id: "1",
    source_namespace: "ethereum",
    native_asset_address: "0x0000000000000000000000000000000000000000",
    wrapped_native_asset_address: "0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2",
    native_usd_reference_pool: "0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640",
    native_usd_reference_protocol: uniswap_v3::PROTOCOL_ID,
    native_usd_reference_source:
        "ethereum:uniswap-v3:WETH/USDC:500:0x88e6a0c2ddd26feeb64f039a2c41296fcb3f5640",
    reference_price_id: "ethUsd",
    stable_assets: ETHEREUM_STABLE_ASSETS,
    uniswap_v2_deployments: ETHEREUM_UNISWAP_V2_DEPLOYMENTS,
    pancake_v2_deployments: ETHEREUM_PANCAKE_V2_DEPLOYMENTS,
    uniswap_v3_deployments: ETHEREUM_UNISWAP_V3_DEPLOYMENTS,
    uniswap_v4_deployments: ETHEREUM_UNISWAP_V4_DEPLOYMENTS,
    pancake_v3_deployments: ETHEREUM_PANCAKE_V3_DEPLOYMENTS,
    pancake_infinity_cl_deployments: ETHEREUM_PANCAKE_INFINITY_CL_DEPLOYMENTS,
    pancake_infinity_bin_deployments: ETHEREUM_PANCAKE_INFINITY_BIN_DEPLOYMENTS,
    aerodrome_classic_deployments: ETHEREUM_AERODROME_CLASSIC_DEPLOYMENTS,
    aerodrome_slipstream_deployments: ETHEREUM_AERODROME_SLIPSTREAM_DEPLOYMENTS,
    curve_deployments: ETHEREUM_CURVE_DEPLOYMENTS,
    fermi_swap_deployments: ETHEREUM_FERMI_SWAP_DEPLOYMENTS,
};

pub static BASE_MAINNET: EvmChainDefinition = EvmChainDefinition {
    chain_id: "8453",
    source_namespace: "base",
    native_asset_address: "0x0000000000000000000000000000000000000000",
    wrapped_native_asset_address: "0x4200000000000000000000000000000000000006",
    native_usd_reference_pool: "0x6c561b446416e1a00e8e93e221854d6ea4171372",
    native_usd_reference_protocol: uniswap_v3::PROTOCOL_ID,
    native_usd_reference_source:
        "base:uniswap-v3:WETH/USDC:3000:0x6c561b446416e1a00e8e93e221854d6ea4171372",
    reference_price_id: "baseEthUsd",
    stable_assets: BASE_STABLE_ASSETS,
    uniswap_v2_deployments: BASE_UNISWAP_V2_DEPLOYMENTS,
    pancake_v2_deployments: BASE_PANCAKE_V2_DEPLOYMENTS,
    uniswap_v3_deployments: BASE_UNISWAP_V3_DEPLOYMENTS,
    uniswap_v4_deployments: BASE_UNISWAP_V4_DEPLOYMENTS,
    pancake_v3_deployments: BASE_PANCAKE_V3_DEPLOYMENTS,
    pancake_infinity_cl_deployments: BASE_PANCAKE_INFINITY_CL_DEPLOYMENTS,
    pancake_infinity_bin_deployments: BASE_PANCAKE_INFINITY_BIN_DEPLOYMENTS,
    aerodrome_classic_deployments: BASE_AERODROME_CLASSIC_DEPLOYMENTS,
    aerodrome_slipstream_deployments: BASE_AERODROME_SLIPSTREAM_DEPLOYMENTS,
    curve_deployments: BASE_CURVE_DEPLOYMENTS,
    fermi_swap_deployments: BASE_FERMI_SWAP_DEPLOYMENTS,
};

const BNB_STABLE_ASSETS: &[StableAsset] = &[
    StableAsset {
        symbol: "USDT",
        address: "0x55d398326f99059ff775485246999027b3197955",
    },
    StableAsset {
        symbol: "USDC",
        address: "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d",
    },
    StableAsset {
        symbol: "BUSD",
        address: "0xe9e7cea3dedca5984780bafc599bd69add087d56",
    },
];
const BNB_UNISWAP_V2_DEPLOYMENTS: &[&str] = &["0x8909dc15e40173ff4699343b6eb8132c65e18ec6"];
const BNB_PANCAKE_V2_DEPLOYMENTS: &[&str] = &["0xca143ce32fe78f1f7019d7d551a6402fc5350c73"];
const BNB_UNISWAP_V3_DEPLOYMENTS: &[&str] = &["0xdb1d10011ad0ff90774d0c6bb92e5c5c8b4461f7"];
const BNB_UNISWAP_V4_DEPLOYMENTS: &[&str] = &["0x28e2ea090877bf75740558f6bfb36a5ffee9e9df"];
const BNB_PANCAKE_V3_DEPLOYMENTS: &[&str] = &[pancake_v3::BNB_FACTORY];
const BNB_PANCAKE_INFINITY_CL_DEPLOYMENTS: &[&str] = &[pancake_infinity_cl::BNB_POOL_MANAGER];
const BNB_PANCAKE_INFINITY_BIN_DEPLOYMENTS: &[&str] = &[pancake_infinity_bin::BNB_POOL_MANAGER];
const BNB_AERODROME_CLASSIC_DEPLOYMENTS: &[&str] = &[];
const BNB_AERODROME_SLIPSTREAM_DEPLOYMENTS: &[&str] = &[];
const BNB_CURVE_DEPLOYMENTS: &[&str] = &[];
const BNB_FERMI_SWAP_DEPLOYMENTS: &[&str] = &[];

pub static BNB_MAINNET: EvmChainDefinition = EvmChainDefinition {
    chain_id: "56",
    source_namespace: "bsc",
    native_asset_address: "0x0000000000000000000000000000000000000000",
    wrapped_native_asset_address: "0xbb4cdb9cbd36b01bd1cbaebf2de08d9173bc095c",
    native_usd_reference_pool: "0x172fcd41e0913e95784454622d1c3724f546f849",
    native_usd_reference_protocol: pancake_v3::PROTOCOL_ID,
    native_usd_reference_source:
        "bsc:pancake-v3:WBNB/USDT:100:0x172fcd41e0913e95784454622d1c3724f546f849",
    reference_price_id: "bnbUsd",
    stable_assets: BNB_STABLE_ASSETS,
    uniswap_v2_deployments: BNB_UNISWAP_V2_DEPLOYMENTS,
    pancake_v2_deployments: BNB_PANCAKE_V2_DEPLOYMENTS,
    uniswap_v3_deployments: BNB_UNISWAP_V3_DEPLOYMENTS,
    uniswap_v4_deployments: BNB_UNISWAP_V4_DEPLOYMENTS,
    pancake_v3_deployments: BNB_PANCAKE_V3_DEPLOYMENTS,
    pancake_infinity_cl_deployments: BNB_PANCAKE_INFINITY_CL_DEPLOYMENTS,
    pancake_infinity_bin_deployments: BNB_PANCAKE_INFINITY_BIN_DEPLOYMENTS,
    aerodrome_classic_deployments: BNB_AERODROME_CLASSIC_DEPLOYMENTS,
    aerodrome_slipstream_deployments: BNB_AERODROME_SLIPSTREAM_DEPLOYMENTS,
    curve_deployments: BNB_CURVE_DEPLOYMENTS,
    fermi_swap_deployments: BNB_FERMI_SWAP_DEPLOYMENTS,
};

const ROBINHOOD_STABLE_ASSETS: &[StableAsset] = &[StableAsset {
    symbol: "USDG",
    address: "0x5fc5360d0400a0fd4f2af552add042d716f1d168",
}];
const ROBINHOOD_UNISWAP_V2_DEPLOYMENTS: &[&str] = &["0x8bceaa40b9acdfaedf85adf4ff01f5ad6517937f"];
const ROBINHOOD_PANCAKE_V2_DEPLOYMENTS: &[&str] = &[];
const ROBINHOOD_UNISWAP_V3_DEPLOYMENTS: &[&str] = &["0x1f7d7550b1b028f7571e69a784071f0205fd2efa"];
const ROBINHOOD_UNISWAP_V4_DEPLOYMENTS: &[&str] = &["0x8366a39cc670b4001a1121b8f6a443a643e40951"];
const ROBINHOOD_PANCAKE_V3_DEPLOYMENTS: &[&str] = &[];
const ROBINHOOD_PANCAKE_INFINITY_CL_DEPLOYMENTS: &[&str] = &[];
const ROBINHOOD_PANCAKE_INFINITY_BIN_DEPLOYMENTS: &[&str] = &[];
const ROBINHOOD_AERODROME_CLASSIC_DEPLOYMENTS: &[&str] = &[];
const ROBINHOOD_AERODROME_SLIPSTREAM_DEPLOYMENTS: &[&str] = &[];
const ROBINHOOD_CURVE_DEPLOYMENTS: &[&str] = &[];
const ROBINHOOD_FERMI_SWAP_DEPLOYMENTS: &[&str] = &[];

pub static ROBINHOOD_MAINNET: EvmChainDefinition = EvmChainDefinition {
    chain_id: "4663",
    source_namespace: "robinhood",
    native_asset_address: "0x0000000000000000000000000000000000000000",
    wrapped_native_asset_address: "0x0bd7d308f8e1639fab988df18a8011f41eacad73",
    native_usd_reference_pool: "0x52e65b17fb6e5ba00ed806f37afcd2daa50271ca",
    native_usd_reference_protocol: uniswap_v3::PROTOCOL_ID,
    native_usd_reference_source:
        "robinhood:uniswap-v3:WETH/USDG:100:0x52e65b17fb6e5ba00ed806f37afcd2daa50271ca",
    reference_price_id: "robinhoodEthUsd",
    stable_assets: ROBINHOOD_STABLE_ASSETS,
    uniswap_v2_deployments: ROBINHOOD_UNISWAP_V2_DEPLOYMENTS,
    pancake_v2_deployments: ROBINHOOD_PANCAKE_V2_DEPLOYMENTS,
    uniswap_v3_deployments: ROBINHOOD_UNISWAP_V3_DEPLOYMENTS,
    uniswap_v4_deployments: ROBINHOOD_UNISWAP_V4_DEPLOYMENTS,
    pancake_v3_deployments: ROBINHOOD_PANCAKE_V3_DEPLOYMENTS,
    pancake_infinity_cl_deployments: ROBINHOOD_PANCAKE_INFINITY_CL_DEPLOYMENTS,
    pancake_infinity_bin_deployments: ROBINHOOD_PANCAKE_INFINITY_BIN_DEPLOYMENTS,
    aerodrome_classic_deployments: ROBINHOOD_AERODROME_CLASSIC_DEPLOYMENTS,
    aerodrome_slipstream_deployments: ROBINHOOD_AERODROME_SLIPSTREAM_DEPLOYMENTS,
    curve_deployments: ROBINHOOD_CURVE_DEPLOYMENTS,
    fermi_swap_deployments: ROBINHOOD_FERMI_SWAP_DEPLOYMENTS,
};

pub fn by_chain_id(chain_id: &str) -> Option<&'static EvmChainDefinition> {
    match chain_id {
        "1" => Some(&ETHEREUM_MAINNET),
        "8453" => Some(&BASE_MAINNET),
        "56" => Some(&BNB_MAINNET),
        "4663" => Some(&ROBINHOOD_MAINNET),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ethereum_definition_contains_only_allowlisted_deployments() {
        assert!(ETHEREUM_MAINNET
            .supports_deployment(uniswap_v2::PROTOCOL_ID, uniswap_v2::MAINNET_FACTORY));
        assert!(ETHEREUM_MAINNET
            .supports_deployment(uniswap_v3::PROTOCOL_ID, uniswap_v3::SHIBASWAP_V2_FACTORY));
        assert!(ETHEREUM_MAINNET
            .supports_deployment(uniswap_v4::PROTOCOL_ID, uniswap_v4::MAINNET_POOL_MANAGER));
        assert!(!ETHEREUM_MAINNET
            .supports_deployment(uniswap_v2::PROTOCOL_ID, uniswap_v3::MAINNET_FACTORY));
    }

    #[test]
    fn base_definition_is_chain_scoped_and_v4_uses_its_own_manager() {
        assert!(BASE_MAINNET.supports_deployment(
            uniswap_v2::PROTOCOL_ID,
            "0x8909dc15e40173ff4699343b6eb8132c65e18ec6"
        ));
        assert!(BASE_MAINNET.supports_deployment(
            uniswap_v3::PROTOCOL_ID,
            "0x33128a8fc17869897dce68ed026d694621f6fdfd"
        ));
        assert!(BASE_MAINNET.supports_deployment(
            uniswap_v4::PROTOCOL_ID,
            "0x498581ff718922c3f8e6a244956af099b2652b2b"
        ));
        assert!(BASE_MAINNET
            .supports_deployment(pancake_v2::PROTOCOL_ID, BASE_PANCAKE_V2_DEPLOYMENTS[0]));
        assert!(BASE_MAINNET
            .supports_deployment(pancake_v3::PROTOCOL_ID, BASE_PANCAKE_V3_DEPLOYMENTS[0]));
        assert!(BASE_MAINNET.supports_deployment(
            pancake_infinity_cl::PROTOCOL_ID,
            BASE_PANCAKE_INFINITY_CL_DEPLOYMENTS[0]
        ));
        assert!(BASE_MAINNET.supports_deployment(
            pancake_infinity_bin::PROTOCOL_ID,
            BASE_PANCAKE_INFINITY_BIN_DEPLOYMENTS[0]
        ));
        assert!(BASE_MAINNET.supports_deployment(
            aerodrome_classic::PROTOCOL_ID,
            aerodrome_classic::BASE_FACTORY
        ));
        assert!(!ETHEREUM_MAINNET.supports_deployment(
            aerodrome_classic::PROTOCOL_ID,
            aerodrome_classic::BASE_FACTORY
        ));
        assert!(BASE_MAINNET.supports_deployment(
            aerodrome_slipstream::PROTOCOL_ID,
            aerodrome_slipstream::BASE_GAUGES_V3_FACTORY
        ));
        assert!(BASE_MAINNET
            .supports_slipstream_tick_spacing(aerodrome_slipstream::BASE_INITIAL_FACTORY, 100));
        assert!(!BASE_MAINNET
            .supports_slipstream_tick_spacing(aerodrome_slipstream::BASE_INITIAL_FACTORY, 500));
        assert!(!BASE_MAINNET
            .supports_deployment(uniswap_v4::PROTOCOL_ID, uniswap_v4::MAINNET_POOL_MANAGER));
        assert!(
            !BASE_MAINNET.supports_deployment(uniswap_v2::PROTOCOL_ID, uniswap_v2::MAINNET_FACTORY)
        );
        assert_eq!(
            BASE_MAINNET.stable_symbol(BASE_STABLE_ASSETS[0].address),
            Some("USDC")
        );
    }

    #[test]
    fn bnb_definition_allowlists_canonical_pancake_and_uniswap_deployments() {
        assert!(BNB_MAINNET.supports_deployment(
            pancake_v2::PROTOCOL_ID,
            "0xca143ce32fe78f1f7019d7d551a6402fc5350c73"
        ));
        assert!(!BNB_MAINNET.supports_deployment(
            uniswap_v2::PROTOCOL_ID,
            "0xca143ce32fe78f1f7019d7d551a6402fc5350c73"
        ));
        assert!(BNB_MAINNET.supports_deployment(pancake_v3::PROTOCOL_ID, pancake_v3::BNB_FACTORY));
        assert!(BNB_MAINNET.supports_deployment(
            pancake_infinity_cl::PROTOCOL_ID,
            pancake_infinity_cl::BNB_POOL_MANAGER
        ));
        assert!(BNB_MAINNET.supports_deployment(
            pancake_infinity_bin::PROTOCOL_ID,
            pancake_infinity_bin::BNB_POOL_MANAGER
        ));
        assert!(
            BNB_MAINNET.supports_deployment(uniswap_v2::PROTOCOL_ID, BNB_UNISWAP_V2_DEPLOYMENTS[0])
        );
        assert!(
            BNB_MAINNET.supports_deployment(uniswap_v3::PROTOCOL_ID, BNB_UNISWAP_V3_DEPLOYMENTS[0])
        );
        assert!(
            BNB_MAINNET.supports_deployment(uniswap_v4::PROTOCOL_ID, BNB_UNISWAP_V4_DEPLOYMENTS[0])
        );
        assert!(!BNB_MAINNET.supports_deployment(uniswap_v3::PROTOCOL_ID, pancake_v3::BNB_FACTORY));
        assert_eq!(
            BNB_MAINNET.stable_symbol(BNB_STABLE_ASSETS[0].address),
            Some("USDT")
        );
    }

    #[test]
    fn robinhood_definition_allowlists_only_canonical_uniswap_deployments() {
        assert!(ROBINHOOD_MAINNET
            .supports_deployment(uniswap_v2::PROTOCOL_ID, ROBINHOOD_UNISWAP_V2_DEPLOYMENTS[0]));
        assert!(ROBINHOOD_MAINNET
            .supports_deployment(uniswap_v3::PROTOCOL_ID, ROBINHOOD_UNISWAP_V3_DEPLOYMENTS[0]));
        assert!(ROBINHOOD_MAINNET
            .supports_deployment(uniswap_v4::PROTOCOL_ID, ROBINHOOD_UNISWAP_V4_DEPLOYMENTS[0]));
        assert!(!ROBINHOOD_MAINNET
            .supports_deployment(pancake_v3::PROTOCOL_ID, ROBINHOOD_UNISWAP_V3_DEPLOYMENTS[0]));
        assert!(ROBINHOOD_MAINNET
            .supports_deployment(pons_v2_curve::PROTOCOL_ID, pons_v2_curve::ROBINHOOD_FACTORY));
        assert!(!ETHEREUM_MAINNET
            .supports_deployment(pons_v2_curve::PROTOCOL_ID, pons_v2_curve::ROBINHOOD_FACTORY));
        assert_eq!(
            ROBINHOOD_MAINNET.stable_symbol(ROBINHOOD_STABLE_ASSETS[0].address),
            Some("USDG")
        );
    }
}
