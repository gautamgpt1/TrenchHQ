use thiserror::Error;

pub const PROGRAM_ID: &str = "LBUZKhRxPF3XUpBCjp4YzTKgLccjZhTSDM9YuVaPwxo";
pub const LB_PAIR_ACCOUNT_LEN: usize = 904;
const LB_PAIR_DISCRIMINATOR: [u8; 8] = [33, 11, 49, 98, 181, 101, 177, 13];
const SWAP_DISCRIMINATORS: [[u8; 8]; 6] = [
    [248, 198, 158, 145, 225, 117, 135, 200],
    [65, 75, 63, 76, 235, 91, 91, 136],
    [250, 73, 101, 33, 38, 207, 75, 184],
    [43, 215, 247, 132, 137, 60, 243, 81],
    [56, 173, 230, 208, 173, 228, 156, 205],
    [74, 98, 192, 214, 177, 51, 75, 51],
];

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MeteoraDlmmPoolState {
    pub active_id: i32,
    pub bin_step: u16,
    pub token_x_mint: String,
    pub token_y_mint: String,
    pub reserve_x: String,
    pub reserve_y: String,
}

#[derive(Clone, Debug, Error, Eq, PartialEq)]
pub enum MeteoraDlmmDecodeError {
    #[error("account owner is not the Meteora DLMM program")]
    WrongOwner,
    #[error("account length is not the supported Meteora DLMM LbPair layout")]
    WrongLength,
    #[error("account discriminator is not a Meteora DLMM LbPair")]
    WrongDiscriminator,
    #[error("DLMM bin step is zero")]
    ZeroBinStep,
}

pub fn is_swap_instruction(data: &[u8]) -> bool {
    SWAP_DISCRIMINATORS
        .iter()
        .any(|discriminator| data.starts_with(discriminator))
}

pub fn decode_pool(
    owner_program: &str,
    data: &[u8],
) -> Result<MeteoraDlmmPoolState, MeteoraDlmmDecodeError> {
    if owner_program != PROGRAM_ID {
        return Err(MeteoraDlmmDecodeError::WrongOwner);
    }
    if data.len() != LB_PAIR_ACCOUNT_LEN {
        return Err(MeteoraDlmmDecodeError::WrongLength);
    }
    if data[..8] != LB_PAIR_DISCRIMINATOR {
        return Err(MeteoraDlmmDecodeError::WrongDiscriminator);
    }
    let bin_step = u16_at(data, 80);
    if bin_step == 0 {
        return Err(MeteoraDlmmDecodeError::ZeroBinStep);
    }

    Ok(MeteoraDlmmPoolState {
        active_id: i32_at(data, 76),
        bin_step,
        token_x_mint: pubkey(data, 88),
        token_y_mint: pubkey(data, 120),
        reserve_x: pubkey(data, 152),
        reserve_y: pubkey(data, 184),
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
    fn decodes_active_bin_and_pair_addresses() {
        let mut data = vec![0_u8; LB_PAIR_ACCOUNT_LEN];
        data[..8].copy_from_slice(&LB_PAIR_DISCRIMINATOR);
        data[76..80].copy_from_slice(&(-12_i32).to_le_bytes());
        data[80..82].copy_from_slice(&25_u16.to_le_bytes());
        data[88..120].copy_from_slice(&[1; 32]);
        data[120..152].copy_from_slice(&[2; 32]);
        data[152..184].copy_from_slice(&[3; 32]);
        data[184..216].copy_from_slice(&[4; 32]);

        let state = decode_pool(PROGRAM_ID, &data).unwrap();
        assert_eq!(state.active_id, -12);
        assert_eq!(state.bin_step, 25);
        assert_eq!(state.reserve_y, bs58::encode([4_u8; 32]).into_string());
    }
}
