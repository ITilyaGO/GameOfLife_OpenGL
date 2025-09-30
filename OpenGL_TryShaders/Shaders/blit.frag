#version 330 core
out vec4 FragColor;
in vec2 vUV;

uniform sampler2D currentState;
uniform sampler2D typeColors;

void main()
{
    vec4 c = texture(currentState, vUV);

    if (c.r < 0.5)
    {
        FragColor = vec4(0.0, 0.0, 0.0, 1.0); 
    }
    else
    {
        int type = int(c.g * 255.0 + 0.5);
        FragColor = texelFetch(typeColors, ivec2(type, 0), 0);
    }
}
