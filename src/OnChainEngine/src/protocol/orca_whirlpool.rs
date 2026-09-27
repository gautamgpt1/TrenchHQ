use thiserror::Error;

pub const PROGRAM_ID: &str = "whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc";
pub const IMMUTABLE_PROGRAM_ID: &str = "iwhrLHdsgrvmnwU8GF2FSmyabSMjfHwFGJAX2ufJ3ZN";
pub const WHIRLPOOL_ACCOUNT_LEN: usize = 653;
const WHIRLPOOL_DISCRIMINATOR: [u8; 8] = [63, 149, 209, 12, 225, 128, 99, 9];
const SWAP_DISCRIMINATORS: [[u8; 8]; 4] = [
    [248, 198, 158, 145, 225, 117, 135, 200],
    [43, 4, 237, 11, 26, 201, 30, 98],
    [195, 96, 237, 108, 68, 162, 219, 230],
    [186, 143, 209, 29, 254, 2, 194, 117],
];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OrcaWhirlpoolState {
    pub tick_spacing: u16,
    pub liquidity: u128,
    pub sqrt_price_x64: u128,
    pub tick_current_index: i32,
    pub token_mint_a: String,
    pub token_vault_a: String,
    pub token_mint_b: String,
    pub token_vault_b: String,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum OrcaWhirlpoolDecodeError {
    #[error("account owner is not the Orca Whirlpool program")]
    WrongOwner,
    #[error("account length is not the supported Orca Whirlpool layout")]
    WrongLength,
    #[error("account discriminator is not an Orca Whirlpool")]
    WrongDiscriminator,
}

pub fn is_swap_instruction(data: &[u8]) -> bool {
    SWAP_DISCRIMINATORS
        .iter()
        .any(|discriminator| data.starts_with(discriminator))
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<OrcaWhirlpoolState, OrcaWhirlpoolDecodeError> {
    if owner_program != PROGRAM_ID && owner_program != IMMUTABLE_PROGRAM_ID {
        return Err(OrcaWhirlpoolDecodeError::WrongOwner);
    }
    if data.len() != WHIRLPOOL_ACCOUNT_LEN {
        return Err(OrcaWhirlpoolDecodeError::WrongLength);
    }
    if data[..8] != WHIRLPOOL_DISCRIMINATOR {
        return Err(OrcaWhirlpoolDecodeError::WrongDiscriminator);
    }

    Ok(OrcaWhirlpoolState {
        tick_spacing: u16_at(data, 41),
        liquidity: u128_at(data, 49),
        sqrt_price_x64: u128_at(data, 65),
        tick_current_index: i32_at(data, 81),
        token_mint_a: pubkey(data, 101),
        token_vault_a: pubkey(data, 133),
        token_mint_b: pubkey(data, 181),
        token_vault_b: pubkey(data, 213),
    })
}

fn pubkey(data: &[u8], offset: usize) -> String {
    bs58::encode(&data[offset..offset + 32]).into_string()
}

fn u16_at(data: &[u8], offset: usize) -> u16 {
    u16::from_le_bytes(
        data[offset..offset + 2]
            .try_into()
            .expect("validated layout"),
    )
}

fn u128_at(data: &[u8], offset: usize) -> u128 {
    u128::from_le_bytes(
        data[offset..offset + 16]
            .try_into()
            .expect("validated layout"),
    )
}

fn i32_at(data: &[u8], offset: usize) -> i32 {
    i32::from_le_bytes(
        data[offset..offset + 4]
            .try_into()
            .expect("validated layout"),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_the_stable_whirlpool_prefix() {
        let mut data = vec![0_u8; WHIRLPOOL_ACCOUNT_LEN];
        data[..8].copy_from_slice(&WHIRLPOOL_DISCRIMINATOR);
        data[41..43].copy_from_slice(&64_u16.to_le_bytes());
        data[49..65].copy_from_slice(&123_u128.to_le_bytes());
        data[65..81].copy_from_slice(&(1_u128 << 64).to_le_bytes());
        data[81..85].copy_from_slice(&(-7_i32).to_le_bytes());
        data[101..133].copy_from_slice(&[1; 32]);
        data[133..165].copy_from_slice(&[2; 32]);
        data[181..213].copy_from_slice(&[3; 32]);
        data[213..245].copy_from_slice(&[4; 32]);

        let state = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.sqrt_price_x64, 1_u128 << 64);
        assert_eq!(state.tick_current_index, -7);
        assert_eq!(state.token_mint_b, bs58::encode([3_u8; 32]).into_string());

        assert!(decode_pool(IMMUTABLE_PROGRAM_ID, &data).is_ok());
    }
}
