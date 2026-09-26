use thiserror::Error;

pub const PROGRAM_ID: &str = "675kPX9MHTjS2zt1qfr1NYHuzeLXfQM9H24wFSUt1Mp8";
pub const POOL_ACCOUNT_LEN: usize = 752;
const SWAP_INSTRUCTION_LEN: usize = 17;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RaydiumAmmV4PoolState {
    pub status: u64,
    pub coin_decimals: u64,
    pub pc_decimals: u64,
    pub need_take_pnl_coin: u64,
    pub need_take_pnl_pc: u64,
    pub coin_vault: String,
    pub pc_vault: String,
    pub coin_mint: String,
    pub pc_mint: String,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum RaydiumAmmV4DecodeError {
    #[error("account owner is not the Raydium AMM v4 program")]
    WrongOwner,
    #[error("account length is not the supported Raydium AMM v4 AmmInfo layout")]
    WrongLength,
    #[error("Raydium AMM v4 status is invalid")]
    InvalidStatus,
}

pub fn is_swap_instruction(data: &[u8]) -> bool {
    data.len() == SWAP_INSTRUCTION_LEN && matches!(data.first(), Some(9 | 11 | 16 | 17))
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<RaydiumAmmV4PoolState, RaydiumAmmV4DecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(RaydiumAmmV4DecodeError::WrongOwner);
    }
    if data.len() != POOL_ACCOUNT_LEN {
        return Err(RaydiumAmmV4DecodeError::WrongLength);
    }
    let status = u64_at(data, 0);
    if !(1..=7).contains(&status) {
        return Err(RaydiumAmmV4DecodeError::InvalidStatus);
    }

    Ok(RaydiumAmmV4PoolState {
        status,
        coin_decimals: u64_at(data, 32),
        pc_decimals: u64_at(data, 40),
        need_take_pnl_coin: u64_at(data, 192),
        need_take_pnl_pc: u64_at(data, 200),
        coin_vault: pubkey(data, 336),
        pc_vault: pubkey(data, 368),
        coin_mint: pubkey(data, 400),
        pc_mint: pubkey(data, 432),
    })
}

fn pubkey(data: &[u8], offset: usize) -> String {
    bs58::encode(&data[offset..offset + 32]).into_string()
}

fn u64_at(data: &[u8], offset: usize) -> u64 {
    u64::from_le_bytes(
        data[offset..offset + 8]
            .try_into()
            .expect("validated layout"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use base64::engine::general_purpose::STANDARD as BASE64;
    use base64::Engine as _;

    #[test]
    fn decodes_recorded_mainnet_sol_usdc_pool() {
        let data = BASE64.decode("BgAAAAAAAAD+AAAAAAAAAAcAAAAAAAAAAwAAAAAAAAAJAAAAAAAAAAYAAAAAAAAAAgAAAAAAAAAAAAAAAAAAAEBCDwAAAAAA9AEAAAAAAAAAAAAAAAAAAEBCDwAAAAAAQEIPAAAAAAABAAAAAAAAAADKmjsAAAAAAMqaOwAAAAAFAAAAAAAAABAnAAAAAAAAGQAAAAAAAAAQJwAAAAAAAAwAAAAAAAAAZAAAAAAAAAAZAAAAAAAAABAnAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE64FyutAwAALkI4/Jo0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACzqVos/TzgAAAAAAAAAAABzVLPxIFgFAAAAAAAAAAAAMWNjiHMDAACZN03ShGQFAAAAAAAAAAAAQEusDaXyOAAAAAAAAAAAAEiYZsIJJAAAuHDhLdN5iRVh0un6jyZDGDTrc28vJPwqKk3/H9XcpN/yy7m3YO3bGFcGMDBjrTPXtXKW6gLU4DNeMc6vpMxC3QabiFf+q4GE+2h/Y0YYwDXaxDncGus7VZig8AAAAAABxvp6877brTo9ZfNqq8l0MbG75MLS9uDkfKYCA0UvXWFsT5PYWOiP+v6gjENnRJfo5qkywMgxSCYqGuPMx4KexvkvOQ/5YJ6K1De7jkwfGqQ6wF0kMIzKd96FEsVQkpLTasTDzvqfGb9UyNwPXk0c7uUyfSZIKynSsTy6pDRHIY0NB1GoKC2mEwX+KZw3uZjlhHHbETUDcxD4vhBFpgr27qvkPHweIeqm+XyL01XiG9EnlnR1bByOEGxucSuhFtlwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAOW2K2XLO72m9WiI5m/ujmTcVWAZnA+IsR/ic70FnoqhY/05a0U7AABO2XAAAAAAAAUEAAAAAAAAAAAAAAAAAAA=").unwrap();
        let state = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.status, 6);
        assert_eq!(state.coin_decimals, 9);
        assert_eq!(state.pc_decimals, 6);
        assert_eq!(
            state.coin_mint,
            "So11111111111111111111111111111111111111112"
        );
        assert_eq!(
            state.pc_mint,
            "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v"
        );
        assert_eq!(
            state.coin_vault,
            "DQyrAcCrDXQ7NeoqGgDCZwBvWDcYmFCjSb9JtteuvPpz"
        );
        assert_eq!(
            state.pc_vault,
            "HLmqeL62xR1QoZ1HKKbXRrdN1p3phKpxRMb2VVopvBBz"
        );
    }

    #[test]
    fn decodes_current_amm_info_offsets_and_swap_tags() {
        let mut data = vec![0_u8; POOL_ACCOUNT_LEN];
        data[0..8].copy_from_slice(&6_u64.to_le_bytes());
        data[32..40].copy_from_slice(&9_u64.to_le_bytes());
        data[40..48].copy_from_slice(&6_u64.to_le_bytes());
        data[192..200].copy_from_slice(&11_u64.to_le_bytes());
        data[200..208].copy_from_slice(&12_u64.to_le_bytes());
        data[336..368].copy_from_slice(&[1; 32]);
        data[368..400].copy_from_slice(&[2; 32]);
        data[400..432].copy_from_slice(&[3; 32]);
        data[432..464].copy_from_slice(&[4; 32]);

        let state = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.status, 6);
        assert_eq!(state.coin_decimals, 9);
        assert_eq!(state.need_take_pnl_pc, 12);
        assert_eq!(state.coin_vault, bs58::encode([1_u8; 32]).into_string());
        assert_eq!(state.pc_mint, bs58::encode([4_u8; 32]).into_string());

        for tag in [9_u8, 11, 16, 17] {
            let mut instruction = [0_u8; SWAP_INSTRUCTION_LEN];
            instruction[0] = tag;
            assert!(is_swap_instruction(&instruction));
        }
        assert!(!is_swap_instruction(&[9]));
        assert!(!is_swap_instruction(&[10; SWAP_INSTRUCTION_LEN]));
    }

    #[test]
    fn rejects_wrong_owner_length_and_status() {
        let mut data = vec![0_u8; POOL_ACCOUNT_LEN];
        data[0..8].copy_from_slice(&6_u64.to_le_bytes());
        assert_eq!(
            decode_pool("wrong", &data),
            Err(RaydiumAmmV4DecodeError::WrongOwner)
        );
        assert_eq!(
            decode_pool(PROGRAM_ID, &data[..POOL_ACCOUNT_LEN - 1]),
            Err(RaydiumAmmV4DecodeError::WrongLength)
        );
        data[0..8].fill(0);
        assert_eq!(
            decode_pool(PROGRAM_ID, &data),
            Err(RaydiumAmmV4DecodeError::InvalidStatus)
        );
    }
}
